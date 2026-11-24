using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Integrity.Contracts;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Integrity.Tests;

/// <summary>
/// Integrity flags (ADR-0036 §2, §3): another module syncs what a rule finds about a subject, in its own transaction;
/// missing flags open, flags no longer found close, every change is ledgered, nothing is deleted; the portal reads the
/// flags of its sites, never another company's.
/// </summary>
[Trait("Story", "US-P2-INT-01")]
public sealed partial class FlagsTests(IntegrityHost host) : IClassFixture<IntegrityHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Finding Gap(string key, params Guid[] entries) =>
        new(key, [.. entries.Select(e => new FlagSubject("shift_entry", e))], ["sha256:" + new string('a', 64)], new Dictionary<string, string> { ["end"] = "100.0", ["start"] = "104.0" });

    [Fact]
    public async Task Findings_open_flags_once_and_a_finding_gone_closes_its_flag_with_the_reason()
    {
        var tenant = await ProvisionAsync();
        var (meter, site, a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var subject = new FlagSubject("meter", meter);

        await SyncAsync(tenant, subject, site, [Gap("a>b", a, b), Gap("b>c", b, c)]);
        await SyncAsync(tenant, subject, site, [Gap("a>b", a, b), Gap("b>c", b, c)]);
        await SyncAsync(tenant, subject, site, [Gap("b>c", b, c)]);
        await SyncAsync(tenant, subject, site, [Gap("a>b", a, b), Gap("b>c", b, c)]);

        // Flags opened in the same millisecond have UUIDv7 IDs in no particular order: compare as a set.
        var all = await ListAsync(tenant, "?status=all");
        all.Select(f => $"{f.GetProperty("key").GetString()} {(f.GetProperty("closedAt").ValueKind == JsonValueKind.Null ? "open" : f.GetProperty("closeReason").GetString())}")
            .Should().BeEquivalentTo("a>b open", "b>c open", "a>b re-evaluated");
        var newest = all.Single(f => f.GetProperty("key").GetString() == "a>b" && f.GetProperty("closedAt").ValueKind == JsonValueKind.Null);
        (newest.GetProperty("rule").GetString(), newest.GetProperty("severity").GetString(), newest.GetProperty("subject").GetProperty("id").GetGuid()).Should().Be(("RULE-FO-01", "high", meter));
        newest.GetProperty("related").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).Should().Equal(a, b);
        newest.GetProperty("details").GetProperty("start").GetString().Should().Be("104.0");
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'integrity.flag_opened.v1'")).Should().Be(3);
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'integrity.flag_closed.v1'")).Should().Be(1);
    }

    [Fact]
    public async Task A_flag_only_closes_it_is_never_deleted_nor_changed_otherwise()
    {
        var tenant = await ProvisionAsync();
        await SyncAsync(tenant, new FlagSubject("meter", Guid.CreateVersion7()), Guid.CreateVersion7(), [Gap("x", Guid.CreateVersion7())]);

        await FluentActions.Awaiting(() => ExecuteAsync($"DELETE FROM integrity.flags WHERE tenant_id = '{tenant}'")).Should().ThrowAsync<PostgresException>();
        await FluentActions.Awaiting(() => ExecuteAsync($"UPDATE integrity.flags SET severity = 'low' WHERE tenant_id = '{tenant}'")).Should().ThrowAsync<PostgresException>();
        await ExecuteAsync($"UPDATE integrity.flags SET closed_at = now(), close_reason = 'settled' WHERE tenant_id = '{tenant}'");
        await FluentActions.Awaiting(() => ExecuteAsync($"UPDATE integrity.flags SET close_reason = 'changed' WHERE tenant_id = '{tenant}'"))
            .Should().ThrowAsync<PostgresException>("a closed flag stays as it was closed");
    }

    [Fact]
    public async Task The_list_pages_newest_first_filters_and_rejects_what_it_does_not_know()
    {
        var tenant = await ProvisionAsync();
        var site = Guid.CreateVersion7();
        for (var i = 0; i < 3; i++)
        {
            await SyncAsync(tenant, new FlagSubject("meter", Guid.CreateVersion7()), site, [Gap($"k{i}", Guid.CreateVersion7())]);
        }

        var first = await JsonAsync(tenant, "?limit=2");
        var cursor = first.GetProperty("nextCursor").GetGuid();
        var second = await JsonAsync(tenant, $"?limit=2&cursor={cursor}");

        // Pages follow the IDs (UUIDv7: creation order across milliseconds): two pages hold the three flags once each.
        var pages = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(f => f.GetProperty("key").GetString()).ToList();
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        pages.Should().BeEquivalentTo(["k0", "k1", "k2"]);
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        (await ListAsync(tenant, "?rule=RULE-FO-02")).Should().BeEmpty();
        (await GetAsync(tenant, "?rule=RULE-XX-99")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(tenant, "?status=solved")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(tenant, "?limit=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_site_role_reads_the_flags_of_its_sites_a_field_role_none_and_another_company_never()
    {
        var (tenant, other) = (await ProvisionAsync(), await ProvisionAsync());
        var (north, south) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await SyncAsync(tenant, new FlagSubject("meter", Guid.CreateVersion7()), north, [Gap("north", Guid.CreateVersion7())]);
        await SyncAsync(tenant, new FlagSubject("meter", Guid.CreateVersion7()), south, [Gap("south", Guid.CreateVersion7())]);
        host.SiteOf[IntegrityHost.SiteManager] = north;

        var manager = await ListAsync(tenant, "", TestTokens.ForTenant(tenant, IntegrityHost.SiteManager));
        var field = await GetAsync(tenant, "", TestTokens.ForTenant(tenant, IntegrityHost.Operator));
        var elsewhere = await ListAsync(other, "");

        manager.Select(f => f.GetProperty("key").GetString()).Should().Equal("north");
        field.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        elsewhere.Should().BeEmpty();
    }

    /// <summary>A module syncing its findings in its own transaction, as FieldOps does on every fact (ADR-0036 §3).</summary>
    private async Task SyncAsync(Guid tenant, FlagSubject subject, Guid site, IReadOnlyList<Finding> findings)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IIntegrityFlags>().SyncAsync(connection, transaction, subject, IntegrityRules.Continuity, site, findings, Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task<List<JsonElement>> ListAsync(Guid tenant, string query, string? token = null) =>
        [.. (await JsonAsync(tenant, query, token)).GetProperty("items").EnumerateArray()];

    private async Task<JsonElement> JsonAsync(Guid tenant, string query, string? token = null)
    {
        using var response = await GetAsync(tenant, query, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid tenant, string query, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/integrity/flags{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForTenant(tenant, "admin-1"));
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"i-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
