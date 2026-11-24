using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Integrity.Application;
using Karry.Modules.Integrity.Contracts;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Integrity.Tests;

/// <summary>
/// Continuity gaps settled with a reason (US-P2-FO-08, ADR-0037 §7): one person proposes, another approves, a settled
/// gap is never raised again; "unexplained" or 48 h unsettled raises RULE-FO-11 once.
/// </summary>
public sealed partial class FlagsTests
{
    [Fact]
    [Trait("Story", "US-P2-FO-08")]
    public async Task A_gap_settled_by_one_person_and_approved_by_another_closes_with_its_reason_and_is_not_raised_again()
    {
        var tenant = await ProvisionAsync();
        var (meter, site, a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await SyncAsync(tenant, new FlagSubject("meter", meter), site, [Gap("a>b", a, b)]);
        var gap = (await ListAsync(tenant, "")).Single();

        using var proposed = await SettleAsync(tenant, gap, "settle", "admin-1", new { reason = "meter_replaced", comment = "New meter fitted on the 9th, photo in the work order." });
        var proposal = await proposed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        using var own = await SettleAsync(tenant, proposal, "settlement/approve", "admin-1");
        using var approved = await SettleAsync(tenant, proposal, "settlement/approve", "admin-2");
        await SyncAsync(tenant, new FlagSubject("meter", meter), site, [Gap("a>b", a, b)]);

        proposed.StatusCode.Should().Be(HttpStatusCode.OK);
        proposal.GetProperty("settlement").GetProperty("reason").GetString().Should().Be("meter_replaced");
        proposal.GetProperty("settlement").GetProperty("proposedBy").GetString().Should().Be("admin-1");
        proposal.GetProperty("closedAt").ValueKind.Should().Be(JsonValueKind.Null, "a settlement waits for another person");
        own.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeAsync(own)).Should().Be("integrity.sod_own_settlement");
        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        var closed = await approved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        (closed.GetProperty("closeReason").GetString(), closed.GetProperty("closedBy").GetString()).Should().Be(("settled:meter_replaced", "admin-2"));
        (await ListAsync(tenant, "?status=all")).Should().ContainSingle("the gap still holds, but a settled key is never raised again");
        (await LedgerAsync(tenant)).Should().Equal(
            "integrity.flag_opened.v1|RULE-FO-01|", "integrity.flag_settlement_proposed.v1|RULE-FO-01|meter_replaced", "integrity.flag_closed.v1|RULE-FO-01|settled:meter_replaced");
    }

    [Fact]
    [Trait("Story", "US-P2-FO-08")]
    public async Task A_settlement_is_refused_without_a_reason_a_story_a_recent_sign_in_the_permission_or_the_current_version()
    {
        var tenant = await ProvisionAsync();
        var other = await ProvisionAsync();
        await SyncAsync(tenant, new FlagSubject("meter", Guid.CreateVersion7()), Guid.CreateVersion7(), [Gap("a>b", Guid.CreateVersion7(), Guid.CreateVersion7())]);
        var gap = (await ListAsync(tenant, "")).Single();
        var story = "Towed to the workshop.";

        var refusals = new List<(HttpStatusCode, string?)>();
        foreach (var (body, subject, recent, ifMatch, company) in new (object, string, bool, string?, Guid)[]
        {
            (new { reason = "data_entry_error", comment = story }, "admin-1", true, null, tenant),
            (new { reason = "lost", comment = story }, "admin-1", true, null, tenant),
            (new { reason = "towing", comment = " " }, "admin-1", true, null, tenant),
            (new { reason = "towing", comment = story }, "admin-1", false, null, tenant),
            (new { reason = "towing", comment = story }, IntegrityHost.Operator, true, null, tenant),
            (new { reason = "towing", comment = story }, "admin-1", true, null, other),
            (new { reason = "towing", comment = story }, "admin-1", true, "\"1\"", tenant),
        })
        {
            using var response = await SettleAsync(company, gap, "settle", subject, body, recent, ifMatch ?? gap.GetProperty("version").GetString());
            refusals.Add((response.StatusCode, await CodeAsync(response)));
        }

        using var nothing = await SettleAsync(tenant, gap, "settlement/approve", "admin-2");

        refusals.Should().Equal(
            (HttpStatusCode.UnprocessableEntity, "integrity.correct_the_entry"),
            (HttpStatusCode.BadRequest, "integrity.settlement_reason_invalid"),
            (HttpStatusCode.BadRequest, "integrity.settlement_comment_required"),
            (HttpStatusCode.Unauthorized, PermissionMiddleware.StepUpCode),
            (HttpStatusCode.Forbidden, PermissionMiddleware.ForbiddenCode),
            (HttpStatusCode.NotFound, "integrity.flag_not_found"),
            (HttpStatusCode.Conflict, "request.version_conflict"));
        nothing.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ListAsync(tenant, "")).Single().GetProperty("settlement").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    [Trait("Story", "US-P2-FO-08")]
    public async Task Unexplained_raises_fraud_suspected_at_once_and_declining_keeps_the_gap_open()
    {
        var tenant = await ProvisionAsync();
        var meter = Guid.CreateVersion7();
        await SyncAsync(tenant, new FlagSubject("meter", meter), Guid.CreateVersion7(), [Gap("a>b", Guid.CreateVersion7(), Guid.CreateVersion7())]);
        var gap = (await ListAsync(tenant, "")).Single();

        using var proposed = await SettleAsync(tenant, gap, "settle", "admin-1", new { reason = "unexplained", comment = "Nobody knows what ran the engine." });
        using var declined = await SettleAsync(tenant, await proposed.Content.ReadFromJsonAsync<JsonElement>(Ct), "settlement/decline", "admin-2", new { comment = "Ask the night shift first." });
        var run = await host.Services.GetRequiredService<RaiseUnsettledGaps>().RunAtAsync(DateTimeOffset.UtcNow.AddHours(49), Ct);

        declined.StatusCode.Should().Be(HttpStatusCode.OK);
        run.FailedTenants.Should().Be(0);
        var open = await ListAsync(tenant, "");
        var stillOpen = open.Single(f => f.GetProperty("rule").GetString() == IntegrityRules.Continuity);
        stillOpen.GetProperty("settlement").ValueKind.Should().Be(JsonValueKind.Null);
        stillOpen.GetProperty("fraudSuspectedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        var fraud = open.Single(f => f.GetProperty("rule").GetString() == IntegrityRules.FraudSuspected);
        (fraud.GetProperty("severity").GetString(), fraud.GetProperty("subject").GetProperty("id").GetGuid(), fraud.GetProperty("key").GetString())
            .Should().Be(("critical", meter, gap.GetProperty("id").GetGuid().ToString()));
        fraud.GetProperty("details").GetProperty("cause").GetString().Should().Be("unexplained");
        fraud.GetProperty("related")[0].GetProperty("id").GetGuid().Should().Be(gap.GetProperty("id").GetGuid());
        (await LedgerAsync(tenant)).Should().Contain("integrity.flag_settlement_declined.v1|RULE-FO-01|unexplained")
            .And.ContainSingle(e => e.StartsWith("integrity.flag_opened.v1|RULE-FO-11", StringComparison.Ordinal), "raised once, not again after 48 h");
    }

    [Fact]
    [Trait("Story", "US-P2-FO-08")]
    public async Task A_gap_left_unsettled_beyond_48_hours_raises_fraud_suspected_once_and_a_settled_one_does_not()
    {
        var tenant = await ProvisionAsync();
        var (left, settledMeter) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await SyncAsync(tenant, new FlagSubject("meter", left), Guid.CreateVersion7(), [Gap("a>b", Guid.CreateVersion7(), Guid.CreateVersion7())]);
        await SyncAsync(tenant, new FlagSubject("meter", settledMeter), Guid.CreateVersion7(), [Gap("c>d", Guid.CreateVersion7(), Guid.CreateVersion7())]);
        var settled = (await ListAsync(tenant, "")).Single(f => f.GetProperty("subject").GetProperty("id").GetGuid() == settledMeter);
        using (var proposed = await SettleAsync(tenant, settled, "settle", "admin-1", new { reason = "maintenance_run", comment = "Engine run by the fitter, job card 118." }))
        {
            (await SettleAsync(tenant, await proposed.Content.ReadFromJsonAsync<JsonElement>(Ct), "settlement/approve", "admin-2")).Dispose();
        }

        var raise = host.Services.GetRequiredService<RaiseUnsettledGaps>();
        await raise.RunAtAsync(DateTimeOffset.UtcNow.AddHours(47), Ct);
        var early = await FraudAsync(tenant);
        await raise.RunAtAsync(DateTimeOffset.UtcNow.AddHours(49), Ct);
        await raise.RunAtAsync(DateTimeOffset.UtcNow.AddHours(50), Ct);

        early.Should().BeEmpty("not 48 h yet");
        var fraud = (await FraudAsync(tenant)).Should().ContainSingle().Subject;
        fraud.GetProperty("subject").GetProperty("id").GetGuid().Should().Be(left);
        fraud.GetProperty("details").GetProperty("cause").GetString().Should().Be("unsettled_48h");
    }

    [Fact]
    public async Task A_flag_changes_only_by_closing_settling_or_raising_fraud_once_never_after_it_closed()
    {
        var tenant = await ProvisionAsync();
        await SyncAsync(tenant, new FlagSubject("meter", Guid.CreateVersion7()), Guid.CreateVersion7(), [Gap("x", Guid.CreateVersion7())]);
        var where = $"WHERE tenant_id = '{tenant}'";

        await ExecuteAsync($"UPDATE integrity.flags SET settlement_reason = 'towing', settlement_comment = 'x', settlement_proposed_at = now() {where}");
        await ExecuteAsync($"UPDATE integrity.flags SET fraud_suspected_at = now() {where}");

        await FluentActions.Awaiting(() => ExecuteAsync($"UPDATE integrity.flags SET fraud_suspected_at = now() + interval '1 hour' {where}"))
            .Should().ThrowAsync<Npgsql.PostgresException>("RULE-FO-11 is raised once");
        await FluentActions.Awaiting(() => ExecuteAsync($"UPDATE integrity.flags SET key = 'y' {where}")).Should().ThrowAsync<Npgsql.PostgresException>();
        await ExecuteAsync($"UPDATE integrity.flags SET closed_at = now(), close_reason = 'settled:towing' {where}");
        await FluentActions.Awaiting(() => ExecuteAsync($"UPDATE integrity.flags SET settlement_reason = NULL, settlement_comment = NULL, settlement_proposed_at = NULL {where}"))
            .Should().ThrowAsync<Npgsql.PostgresException>("a closed flag stays as it was closed");
    }

    private async Task<List<JsonElement>> FraudAsync(Guid tenant) => await ListAsync(tenant, $"?rule={IntegrityRules.FraudSuspected}");

    private async Task<HttpResponseMessage> SettleAsync(
        Guid tenant, JsonElement flag, string action, string subject, object? body = null, bool recent = true, string? ifMatch = null)
    {
        var claims = new List<(string, string)> { (TenantResolutionMiddleware.TenantClaim, tenant.ToString()) };
        if (recent)
        {
            claims.Add((PermissionMiddleware.AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/integrity/flags/{flag.GetProperty("id").GetGuid()}/{action}")
        {
            Content = JsonContent.Create(body ?? new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(subject, [.. claims]));
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch ?? flag.GetProperty("version").GetString());
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode ? null : (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private async Task<List<string>> LedgerAsync(Guid tenant)
    {
        await using var connection = new Npgsql.NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT event_type || '|' || (payload::jsonb->>'rule') || '|' || coalesce(payload::jsonb->>'reason', '') FROM audit.ledger_entries "
            + $"WHERE tenant_id = '{tenant}' AND event_type LIKE 'integrity.flag%' ORDER BY seq", connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
