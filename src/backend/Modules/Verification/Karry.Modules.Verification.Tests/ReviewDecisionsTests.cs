using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Verification.Contracts;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Verification.Tests;

/// <summary>
/// Approve and reject (ADR-0037 §2–§4): only the version the reviewer saw, one decision per item, never on one's own
/// entry or a machine one drove then, with the permissions of the moment; every decision ledgered with the record's hash.
/// </summary>
[Trait("Story", "US-P2-VER-02")]
public sealed class ReviewDecisionsTests(VerificationHost host) : IClassFixture<VerificationHost>
{
    private const string Hash = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private static readonly DateTimeOffset Morning = new(2026, 11, 23, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_approval_decides_the_item_once_with_the_records_hash_in_the_ledger()
    {
        var tenant = await ProvisionAsync();
        var item = await PendingItemAsync(tenant);

        using var approved = await DecideAsync(tenant, item, "approve", null, "\"" + item.Version + "\"");
        using var again = await DecideAsync(tenant, item, "approve", null, approved.Headers.ETag!.Tag);

        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await approved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().Should().Be("approved");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(again)).Should().Be("verification.item_decided");
        (await RowsAsync($"SELECT payload::jsonb->>'recordHash' || '|' || coalesce(payload::jsonb->>'actor', '-') FROM audit.ledger_entries WHERE tenant_id = '{tenant}' "
            + "AND event_type = 'verification.review_item_approved.v1'")).Should().Equal($"{Hash}|admin-1");
        (await RowsAsync($"SELECT kind || '|' || record_hash FROM verification.review_actions WHERE tenant_id = '{tenant}'")).Should().Equal($"approve|{Hash}");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-VER-008")]
    public async Task Two_controllers_acting_on_one_item_at_once_one_wins_the_other_gets_a_conflict_and_a_stale_version_is_refused()
    {
        var tenant = await ProvisionAsync();
        var item = await PendingItemAsync(tenant);
        var version = "\"" + item.Version + "\"";

        using var stale = await DecideAsync(tenant, item, "approve", null, "\"999\"");
        var both = await Task.WhenAll(
            DecideAsync(tenant, item, "approve", null, version),
            DecideAsync(tenant, item, "reject", new { reasonCode = "wrong_reading", comment = "The end reading does not match the photo." }, version));

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(stale)).Should().Be("request.version_conflict");
        both.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await RowsAsync($"SELECT kind FROM verification.review_actions WHERE tenant_id = '{tenant}'")).Should().ContainSingle("only the winner's decision is kept");
        foreach (var response in both)
        {
            response.Dispose();
        }
    }

    [Fact]
    [Trait("Story", "US-P2-VER-03")]
    public async Task A_rejection_needs_a_known_reason_and_a_comment_and_is_ledgered_with_both()
    {
        var tenant = await ProvisionAsync();
        var item = await PendingItemAsync(tenant);

        using var noReason = await DecideAsync(tenant, item, "reject", new { reasonCode = "bad", comment = "The end reading does not match the photo." });
        using var shortComment = await DecideAsync(tenant, item, "reject", new { reasonCode = "wrong_reading", comment = "no" });
        using var rejected = await DecideAsync(tenant, item, "reject", new { reasonCode = "wrong_reading", comment = "  The end reading does not match the photo.  " });

        (await CodeAsync(noReason)).Should().Be("verification.reason_invalid");
        (await CodeAsync(shortComment)).Should().Be("verification.comment_invalid");
        (await rejected.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().Should().Be("rejected");
        (await RowsAsync($"SELECT payload::jsonb->>'reasonCode' || '|' || (payload::jsonb->>'comment') FROM audit.ledger_entries WHERE tenant_id = '{tenant}' "
            + "AND event_type = 'verification.review_item_rejected.v1'")).Should().Equal("wrong_reading|The end reading does not match the photo.");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-VER-001")]
    public async Task Nobody_decides_on_their_own_entry_nor_on_a_machine_they_drove_then_and_an_unlinked_or_other_person_may()
    {
        var tenant = await ProvisionAsync();
        var (producer, driver, asset) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var own = await PendingItemAsync(tenant, producer: producer, asset: asset);
        var drove = await PendingItemAsync(tenant, asset: asset);
        var other = await PendingItemAsync(tenant, asset: asset);
        host.People.FieldAccountOf["self-1"] = producer;
        host.People.FieldAccountOf["driver-1"] = driver;
        host.Operated.Drove[(driver, asset)] = true;

        using var ownEntry = await DecideAsync(tenant, own, "approve", null, token: TestTokens.ForTenant(tenant, "self-1"));
        using var droveIt = await DecideAsync(tenant, drove, "reject", new { reasonCode = "other", comment = "Looks wrong to me, please check it." }, token: TestTokens.ForTenant(tenant, "driver-1"));
        using var someoneElse = await DecideAsync(tenant, other, "approve", null, token: TestTokens.ForTenant(tenant, "self-1"));

        ownEntry.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeAsync(ownEntry)).Should().Be("verification.sod_own_record");
        droveIt.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeAsync(droveIt)).Should().Be("verification.sod_operated_asset");
        someoneElse.StatusCode.Should().Be(HttpStatusCode.OK, "self-1 neither made this entry nor drove the machine then");
        (await RowsAsync($"SELECT count(*)::text FROM verification.review_actions WHERE tenant_id = '{tenant}'")).Should().Equal("1");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-VER-009")]
    public async Task The_permission_is_read_when_the_decision_is_made_and_another_site_or_company_is_not_found()
    {
        var (tenant, other) = (await ProvisionAsync(), await ProvisionAsync());
        var (north, south) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var item = await PendingItemAsync(tenant, site: north);
        host.SiteOf[VerificationHost.Controller] = north;
        var controller = TestTokens.ForTenant(tenant, VerificationHost.Controller);

        // The controller loaded the queue for north; their role then moves to south before they act.
        (await ListAsync(tenant, controller)).Should().ContainSingle();
        host.SiteOf[VerificationHost.Controller] = south;
        using var moved = await DecideAsync(tenant, item, "approve", null, token: controller);
        using var field = await DecideAsync(tenant, item, "approve", null, token: TestTokens.ForTenant(tenant, VerificationHost.Operator));
        using var elsewhere = await DecideAsync(other, item, "approve", null);

        moved.StatusCode.Should().Be(HttpStatusCode.NotFound);
        field.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        elsewhere.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RowsAsync($"SELECT status FROM verification.review_items WHERE tenant_id = '{tenant}'")).Should().Equal("pending");
    }

    [Fact]
    public async Task A_decision_is_never_changed_or_removed()
    {
        var tenant = await ProvisionAsync();
        var item = await PendingItemAsync(tenant);
        (await DecideAsync(tenant, item, "approve", null)).Dispose();

        await FluentActions.Awaiting(() => ExecuteAsAppAsync(tenant, "UPDATE verification.review_actions SET kind = 'reject'")).Should().ThrowAsync<PostgresException>();
        await FluentActions.Awaiting(() => ExecuteAsAppAsync(tenant, "DELETE FROM verification.review_actions")).Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    [Trait("Story", "US-P2-VER-03")]
    public async Task The_tablet_pulls_the_decisions_of_its_site_over_the_last_14_days_with_the_reason_of_a_rejection()
    {
        var tenant = await ProvisionAsync();
        var (site, elsewhere) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var today = DateTimeOffset.UtcNow.AddHours(-10);
        var approved = await PendingItemAsync(tenant, site: site, from: today);
        var rejected = await PendingItemAsync(tenant, site: site, from: today);
        await PendingItemAsync(tenant, site: site, from: today);
        var other = await PendingItemAsync(tenant, site: elsewhere, from: today);
        var old = await PendingItemAsync(tenant, site: site, from: today.AddDays(-15));
        foreach (var item in new[] { approved, other, old })
        {
            (await DecideAsync(tenant, item, "approve", null)).Dispose();
        }

        (await DecideAsync(tenant, rejected, "reject", new { reasonCode = "photo_unreadable", comment = "The end photo is blurred, retake it." })).Dispose();

        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var delta = (await scope.ServiceProvider.GetServices<Karry.BuildingBlocks.Sync.ISyncSource>().Single().ChangesAsync(new(Guid.CreateVersion7(), site), null, Ct)).Single();

        delta.Name.Should().Be("reviewOutcomes");
        delta.Replace.Should().BeTrue();
        JsonSerializer.Serialize(delta.Upserts, JsonSerializerOptions.Web).Should()
            .Contain($"\"id\":\"{approved.Id}\"").And.Contain($"\"id\":\"{rejected.Id}\"")
            .And.Contain("\"status\":\"rejected\",\"reasonCode\":\"photo_unreadable\",\"comment\":\"The end photo is blurred, retake it.\"")
            .And.NotContain(other.Id.ToString()).And.NotContain(old.Id.ToString());
        delta.Upserts.Should().HaveCount(2, "the pending item is not decided, the other site's and the old one are out of scope");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-VER-002")]
    public async Task A_bulk_approval_is_one_decision_with_one_ledger_entry_listing_every_hash()
    {
        var tenant = await ProvisionAsync();
        var items = new[] { await PendingItemAsync(tenant), await PendingItemAsync(tenant), await PendingItemAsync(tenant) };

        using var response = await BulkAsync(tenant, items);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).Should().Equal("approved", "approved", "approved");
        var batch = body.GetProperty("batchId").GetGuid();
        (await RowsAsync($"SELECT (payload::jsonb->>'batchId') || '|' || jsonb_array_length(payload::jsonb->'itemIds') || '|' || jsonb_array_length(payload::jsonb->'recordHashes') "
            + $"FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'verification.review_items_bulk_approved.v1'")).Should().Equal($"{batch}|3|3");
        (await RowsAsync($"SELECT count(*)::text FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'verification.review_item_approved.v1'"))
            .Should().Equal(["0"], "one ledger entry for the whole decision");
        (await RowsAsync($"SELECT batch_id::text FROM verification.review_actions WHERE tenant_id = '{tenant}'")).Should().Equal(batch.ToString(), batch.ToString(), batch.ToString());
    }

    [Fact]
    [Trait("TestCase", "TC-P2-VER-002")]
    public async Task A_bulk_approval_with_a_flagged_decided_or_own_entry_approves_nothing()
    {
        var tenant = await ProvisionAsync();
        var producer = Guid.CreateVersion7();
        var clean = await PendingItemAsync(tenant);
        var flagged = await PendingItemAsync(tenant, flags: ["RULE-FO-02"]);
        var decided = await PendingItemAsync(tenant);
        (await DecideAsync(tenant, decided, "approve", null)).Dispose();
        var own = await PendingItemAsync(tenant, producer: producer);
        host.People.FieldAccountOf["bulk-self"] = producer;

        using var withFlag = await BulkAsync(tenant, [clean, flagged]);
        using var withDecided = await BulkAsync(tenant, [clean, decided]);
        using var withOwn = await BulkAsync(tenant, [clean, own], TestTokens.ForTenant(tenant, "bulk-self"));
        using var empty = await BulkAsync(tenant, []);
        using var twice = await BulkAsync(tenant, [clean, clean]);

        withFlag.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeAsync(withFlag)).Should().Be("verification.bulk_flagged");
        (await CodeAsync(withDecided)).Should().Be("request.version_conflict", "deciding it moved its version: the batch was made from an older screen");
        (await CodeAsync(withOwn)).Should().Be("verification.sod_own_record");
        (await CodeAsync(empty)).Should().Be("verification.bulk_size");
        (await CodeAsync(twice)).Should().Be("verification.bulk_size");
        (await RowsAsync($"SELECT status FROM verification.review_items WHERE id IN ('{clean.Id}', '{flagged.Id}', '{own.Id}')")).Should().Equal("pending", "pending", "pending");
    }

    private Task<HttpResponseMessage> BulkAsync(Guid tenant, Pending[] items, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/verification/items/approve")
        {
            Content = JsonContent.Create(new { items = items.Select(i => new { id = i.Id, version = "\"" + i.Version + "\"" }) }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForTenant(tenant, "admin-1"));
        return host.Client.SendAsync(request, Ct);
    }

    [Fact]
    [Trait("Story", "US-P2-VER-04")]
    [Trait("TestCase", "TC-P2-VER-004")]
    public async Task A_correction_within_the_thresholds_keeps_both_versions_ledgers_the_diff_and_approves_the_new_one()
    {
        var tenant = await ProvisionAsync();
        var item = await PendingItemAsync(tenant);
        host.Operated.NextCorrection = OperatedAssets.Within;

        using var stale = await CorrectAsync(tenant, item, new { endValue = "107.5" }, "The photo shows 107.5, not 108.0.", TestTokens.ForTenant(tenant, "admin-1"));
        using var shortReason = await CorrectAsync(tenant, item, new { endValue = "107.5" }, "typo");
        using var corrected = await CorrectAsync(tenant, item, new { endValue = "107.5" }, "The photo shows 107.5, not 108.0.");

        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a correction changes a signed fact: a recent sign-in");
        (await CodeAsync(shortReason)).Should().Be("verification.comment_invalid");
        corrected.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await corrected.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("corrected").GetProperty("status").GetString().Should().Be("corrected");
        var next = body.GetProperty("next");
        (next.GetProperty("status").GetString(), next.GetProperty("recordVersion").GetInt32(), next.GetProperty("correctedBy").GetString()).Should().Be(("approved", 3, "admin-1"));
        (await RowsAsync($"SELECT (payload::jsonb->>'fromHash') || '|' || (payload::jsonb->>'toHash') || '|' || (payload::jsonb->'changes'->0->>'before') || '>' "
            + $"|| (payload::jsonb->'changes'->0->>'after') || '|' || (payload::jsonb->>'material') FROM audit.ledger_entries WHERE tenant_id = '{tenant}' "
            + "AND event_type = 'verification.review_item_corrected.v1'")).Should().Equal($"{Hash}|sha256:{new string('e', 64)}|108.0>107.5|false");
        host.Operated.Corrections.Should().Contain(c => c.Version == 2 && c.Fields.ContainsKey("endValue") && c.Fields["endValue"] == "107.5");
    }

    [Fact]
    [Trait("Story", "US-P2-VER-04")]
    [Trait("TestCase", "TC-P2-VER-003")]
    public async Task A_correction_beyond_the_thresholds_waits_for_a_second_approver_who_is_not_the_corrector()
    {
        var tenant = await ProvisionAsync();
        var item = await PendingItemAsync(tenant);
        host.Operated.NextCorrection = new CorrectionOutcome.Corrected(3, "sha256:" + new string('f', 64), [new FieldChange("endValue", "108.0", "112.0")], Material: true);

        using var corrected = await CorrectAsync(tenant, item, new { endValue = "112.0" }, "The meter was misread: 112.0 on the photo.");
        var next = (await corrected.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("next");
        var waiting = new Pending(next.GetProperty("id").GetGuid(), next.GetProperty("version").GetString()!.Trim('"'));
        using var byCorrector = await SecondAsync(tenant, waiting, Recent(tenant, "admin-1"));
        using var notRecent = await SecondAsync(tenant, waiting, TestTokens.ForTenant(tenant, "manager-2"));
        using var bySomeoneElse = await SecondAsync(tenant, waiting, Recent(tenant, "manager-2"));
        using var again = await SecondAsync(tenant, waiting, Recent(tenant, "manager-3"));

        next.GetProperty("status").GetString().Should().Be("awaiting_second");
        byCorrector.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeAsync(byCorrector)).Should().Be("verification.sod_own_correction");
        notRecent.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await bySomeoneElse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().Should().Be("approved");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RowsAsync($"SELECT (payload::jsonb->>'correctedBy') || '|' || (payload::jsonb->>'actor') FROM audit.ledger_entries WHERE tenant_id = '{tenant}' "
            + "AND event_type = 'verification.review_item_second_approved.v1'")).Should().Equal("admin-1|manager-2");
        host.Operated.NextCorrection = OperatedAssets.Within;
    }

    [Fact]
    [Trait("Story", "US-P2-VER-04")]
    public async Task A_correction_the_record_refuses_or_one_of_a_version_that_moved_changes_nothing()
    {
        var tenant = await ProvisionAsync();
        var item = await PendingItemAsync(tenant);

        host.Operated.NextCorrection = new CorrectionOutcome.Refused("verification.field_not_correctable", "Only meter values and fuel.");
        using var refused = await CorrectAsync(tenant, item, new { asset = "x" }, "Wrong machine chosen on the tablet.");
        host.Operated.NextCorrection = new CorrectionOutcome.Moved();
        using var moved = await CorrectAsync(tenant, item, new { endValue = "107.5" }, "The photo shows 107.5, not 108.0.");
        host.Operated.NextCorrection = OperatedAssets.Within;

        (await CodeAsync(refused)).Should().Be("verification.field_not_correctable");
        moved.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RowsAsync($"SELECT status FROM verification.review_items WHERE tenant_id = '{tenant}'")).Should().Equal("pending");
    }

    private Task<HttpResponseMessage> CorrectAsync(Guid tenant, Pending item, object fields, string reason, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/verification/items/{item.Id}/correct") { Content = JsonContent.Create(new { fields, reason }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Recent(tenant, "admin-1"));
        request.Headers.TryAddWithoutValidation("If-Match", "\"" + item.Version + "\"");
        return host.Client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> SecondAsync(Guid tenant, Pending item, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/verification/items/{item.Id}/second-approve");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return host.Client.SendAsync(request, Ct);
    }

    [Fact]
    [Trait("Story", "US-P2-NOT-01")]
    [Trait("TestCase", "TC-P2-NOT-001")]
    public async Task A_rejection_notifies_the_entrys_operator_and_an_escalation_the_role_it_names_once_through_the_outbox()
    {
        var tenant = await ProvisionAsync();
        var (operatorId, site, manager) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        host.Audiences.Holders[("site-manager", site)] = [manager];
        var rejected = await PendingItemAsync(tenant, site: site, producer: operatorId);
        await PendingItemAsync(tenant, site: site);
        (await DecideAsync(tenant, rejected, "reject", new { reasonCode = "wrong_reading", comment = "The photo shows 1 248, not 1 250." }, $"\"{rejected.Version}\""))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        // Overdue now, escalated at the real time: an event dated in the future would wait in the outbox until then.
        await ExecuteAsync($"UPDATE verification.review_items SET due_at = now() - interval '1 hour' WHERE tenant_id = '{tenant}' AND status = 'pending'");
        await host.Services.GetRequiredService<Application.EscalateOverdueReviews>().RunAtAsync(DateTimeOffset.UtcNow, Ct);

        var dispatcher = host.Services.GetRequiredService<OutboxDispatcher<Infrastructure.VerificationDbContext>>();
        await dispatcher.DispatchPendingAsync(1000, Ct);
        await dispatcher.DispatchPendingAsync(1000, Ct);

        (await RowsAsync($"SELECT type || '|' || recipient_id || '|' || (parameters::jsonb->>'reasonCode') FROM notifications.notifications WHERE tenant_id = '{tenant}' AND type = 'review.rejected'"))
            .Should().Equal([$"review.rejected|{operatorId}|wrong_reading"], "the entry's operator, once");
        (await RowsAsync($"SELECT recipient_id::text FROM notifications.notifications WHERE tenant_id = '{tenant}' AND type = 'review.escalated'"))
            .Should().Equal([manager.ToString()], "the policy's escalation role on the site; the rejected one no longer waits");
    }

    private static string Recent(Guid tenant, string subject) =>
        TestTokens.Create(subject, (TenantResolutionMiddleware.TenantClaim, tenant.ToString()),
            (PermissionMiddleware.AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private sealed record Pending(Guid Id, string Version);

    private async Task<Pending> PendingItemAsync(
        Guid tenant, Guid? site = null, Guid? producer = null, Guid? asset = null, DateTimeOffset? from = null, IReadOnlyList<string>? flags = null)
    {
        var start = from ?? Morning;
        var submission = new ReviewSubmission(ReviewRecordTypes.ShiftEntry, Guid.CreateVersion7(), 2, Hash, site ?? Guid.CreateVersion7(), producer ?? Guid.CreateVersion7(),
            asset ?? Guid.CreateVersion7(), start, start.AddHours(8), flags ?? []);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
            await using var connection = new NpgsqlConnection(host.Database.App);
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<IReviewIntake>().SubmitAsync(connection, transaction, submission, Ct);
            await transaction.CommitAsync(Ct);
        }

        var item = (await ListAsync(tenant, null)).Single(i => i.GetProperty("recordId").GetGuid() == submission.RecordId);
        return new Pending(item.GetProperty("id").GetGuid(), item.GetProperty("version").GetString()!.Trim('"'));
    }

    private async Task<HttpResponseMessage> DecideAsync(Guid tenant, Pending item, string action, object? body, string? ifMatch = null, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/verification/items/{item.Id}/{action}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForTenant(tenant, "admin-1"));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (action == "reject")
        {
            request.Content = JsonContent.Create(body);
        }

        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<List<JsonElement>> ListAsync(Guid tenant, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/verification/items?status=all");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForTenant(tenant, "admin-1"));
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()];
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"d-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<string>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task ExecuteAsAppAsync(Guid tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Ct);
        await transaction.CommitAsync(Ct);
    }
}
