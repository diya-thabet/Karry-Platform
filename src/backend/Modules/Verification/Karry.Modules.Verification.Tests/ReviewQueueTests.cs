using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Verification.Contracts;
using Karry.Modules.Verification.Domain;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Verification.Tests;

/// <summary>
/// The controller's queue (US-P2-VER-01, ADR-0038 §1, §3): risk order on the server (overdue, flagged, the rest, each
/// oldest first) with a cursor that pages without gaps; filters by asset, producer, flag and covered time; an unchanged
/// page answers 304 to a poll.
/// </summary>
[Trait("Story", "US-P2-VER-01")]
public sealed class ReviewQueueTests(VerificationHost host) : IClassFixture<VerificationHost>
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 23, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_item_ranks_flagged_when_submitted_with_flags_and_overdue_once_escalated()
    {
        var clock = TimeProvider.System;
        var plain = ReviewItem.Submit(clock, new TenantId(Guid.CreateVersion7()), ReviewRecordTypes.ShiftEntry, Guid.CreateVersion7(), 2, "sha256:" + new string('a', 64),
            Guid.CreateVersion7(), Guid.CreateVersion7(), null, Morning, Morning.AddHours(8), []);
        var flagged = ReviewItem.Submit(clock, new TenantId(Guid.CreateVersion7()), ReviewRecordTypes.ShiftEntry, Guid.CreateVersion7(), 2, "sha256:" + new string('a', 64),
            Guid.CreateVersion7(), Guid.CreateVersion7(), null, Morning, Morning.AddHours(8), ["RULE-FO-01"]);
        plain.Pin(clock, EffectivePolicy.Defaults);

        (plain.Risk, flagged.Risk).Should().Be((ReviewRisk.None, ReviewRisk.Flagged));
        plain.Escalate(clock, plain.DueAt.AddMinutes(1), "site-manager").Should().NotBeNull();
        plain.Risk.Should().Be(ReviewRisk.Overdue);
    }

    [Fact]
    public async Task By_risk_overdue_come_first_then_flagged_then_the_rest_each_oldest_first_and_pages_have_no_gaps()
    {
        var tenant = await ProvisionAsync();
        var site = Guid.CreateVersion7();
        var plainOld = await SubmitAsync(tenant, Entry(site));
        var flaggedOld = await SubmitAsync(tenant, Entry(site, flags: ["RULE-FO-01"]));
        var overdue = await SubmitAsync(tenant, Entry(site));
        var plainNew = await SubmitAsync(tenant, Entry(site));
        var flaggedNew = await SubmitAsync(tenant, Entry(site, flags: ["RULE-FO-03"]));
        await ExecuteAsync($"UPDATE verification.review_items SET escalated_at = now(), risk = {ReviewRisk.Overdue} WHERE record_id = '{overdue}'");

        var all = (await ListAsync(tenant, "?sort=risk")).Select(RecordOf).ToList();
        var paged = new List<Guid>();
        Guid? cursor = null;
        do
        {
            var page = await JsonAsync(tenant, "?sort=risk&limit=2" + (cursor is { } c ? $"&cursor={c}" : ""));
            paged.AddRange(page.GetProperty("items").EnumerateArray().Select(RecordOf));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetGuid();
        }
        while (cursor is not null);

        all.Should().Equal(overdue, flaggedOld, flaggedNew, plainOld, plainNew);
        paged.Should().Equal(all, "the cursor carries the risk of the last item of the page");
        (await ListAsync(tenant, "")).Select(RecordOf).Should().Equal([plainOld, flaggedOld, overdue, plainNew, flaggedNew], "by default the oldest come first");
    }

    [Fact]
    public async Task The_queue_filters_by_asset_producer_flag_and_covered_time_and_refuses_what_it_does_not_know()
    {
        var tenant = await ProvisionAsync();
        var (site, asset, driver) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var onAsset = await SubmitAsync(tenant, Entry(site) with { AssetId = asset });
        var byDriver = await SubmitAsync(tenant, Entry(site) with { ProducedBy = driver });
        var skewed = await SubmitAsync(tenant, Entry(site, flags: ["RULE-FO-03", "RULE-FO-04"]));
        var nextDay = await SubmitAsync(tenant, Entry(site) with { CoveredFrom = Morning.AddDays(1), CoveredTo = Morning.AddDays(1).AddHours(8) });

        (await ListAsync(tenant, $"?assetId={asset}")).Select(RecordOf).Should().Equal(onAsset);
        (await ListAsync(tenant, $"?producedBy={driver}")).Select(RecordOf).Should().Equal(byDriver);
        (await ListAsync(tenant, "?flag=RULE-FO-04")).Select(RecordOf).Should().Equal(skewed);
        (await ListAsync(tenant, "?flag=RULE-FO-01")).Should().BeEmpty();
        (await ListAsync(tenant, $"?coveredFrom={Uri.EscapeDataString(Morning.AddHours(12).ToString("O"))}")).Select(RecordOf).Should().Equal(nextDay);
        (await ListAsync(tenant, $"?coveredBefore={Uri.EscapeDataString(Morning.AddHours(12).ToString("O"))}")).Should().HaveCount(3);
        var refusals = new List<string?>();
        foreach (var query in new[] { "?sort=newest", "?flag=a%27%20OR%201%3D1", $"?flag={new string('F', 61)}",
            $"?coveredFrom={Uri.EscapeDataString(Morning.ToString("O"))}&coveredBefore={Uri.EscapeDataString(Morning.ToString("O"))}" })
        {
            using var response = await GetAsync(tenant, query);
            refusals.Add(response.StatusCode == HttpStatusCode.BadRequest ? (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString() : null);
        }

        refusals.Should().Equal("verification.sort_invalid", "verification.flag_invalid", "verification.flag_invalid", "verification.range_invalid");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-VER-010")]
    public async Task A_poll_of_an_unchanged_page_is_a_304_without_a_body_and_a_new_entry_changes_the_tag()
    {
        var tenant = await ProvisionAsync();
        var site = Guid.CreateVersion7();
        await SubmitAsync(tenant, Entry(site));

        using var first = await GetAsync(tenant, "?sort=risk");
        var tag = first.Headers.ETag?.ToString();
        using var unchanged = await GetAsync(tenant, "?sort=risk", tag);
        var flagged = await SubmitAsync(tenant, Entry(site, flags: ["RULE-FO-01"]));
        using var changed = await GetAsync(tenant, "?sort=risk", tag);

        tag.Should().NotBeNullOrEmpty();
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await unchanged.Content.ReadAsByteArrayAsync(Ct)).Should().BeEmpty();
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        changed.Headers.ETag?.ToString().Should().NotBe(tag);
        RecordOf((await changed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items")[0]).Should().Be(flagged, "a new flagged entry comes first");
    }

    [Fact]
    [Trait("Story", "US-P2-VER-06")]
    public async Task A_sites_counts_for_the_daily_digest_are_its_waiting_items_those_overdue_and_the_days_corrections()
    {
        var tenant = await ProvisionAsync();
        var (site, elsewhere) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var overdue = await SubmitAsync(tenant, Entry(site));
        await SubmitAsync(tenant, Entry(site));
        var correctedToday = await SubmitAsync(tenant, Entry(site));
        var correctedLastWeek = await SubmitAsync(tenant, Entry(site));
        await SubmitAsync(tenant, Entry(elsewhere));
        await ExecuteAsync($"UPDATE verification.review_items SET due_at = now() - interval '1 hour' WHERE record_id = '{overdue}'");
        await ExecuteAsync($"UPDATE verification.review_items SET status = 'approved', corrected_by = 'ctl-1', created_at = now() - interval '2 hours' WHERE record_id = '{correctedToday}'");
        await ExecuteAsync($"UPDATE verification.review_items SET status = 'approved', corrected_by = 'ctl-1', created_at = now() - interval '7 days' WHERE record_id = '{correctedLastWeek}'");

        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var now = DateTimeOffset.UtcNow;
        var counts = await scope.ServiceProvider.GetRequiredService<IReviewCounts>().OfSiteAsync(site, now.AddDays(-1), now, Ct);

        counts.Should().Be(new ReviewCounts(Pending: 2, Overdue: 1, Corrected: 1));
    }

    private static Guid RecordOf(JsonElement item) => item.GetProperty("recordId").GetGuid();

    private static ReviewSubmission Entry(Guid site, params string[] flags) =>
        new(ReviewRecordTypes.ShiftEntry, Guid.CreateVersion7(), 2, "sha256:" + new string('c', 64), site, Guid.CreateVersion7(),
            Guid.CreateVersion7(), Morning, Morning.AddHours(8), flags);

    /// <summary>A module submitting a version in its own transaction, as FieldOps does when an entry ends (ADR-0037 §1).</summary>
    private async Task<Guid> SubmitAsync(Guid tenant, ReviewSubmission submission)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IReviewIntake>().SubmitAsync(connection, transaction, submission, Ct);
        await transaction.CommitAsync(Ct);
        // UUIDv7 IDs minted in the same millisecond have no order: the next item gets a later millisecond.
        await Task.Delay(2, Ct);
        return submission.RecordId;
    }

    private async Task<List<JsonElement>> ListAsync(Guid tenant, string query) =>
        [.. (await JsonAsync(tenant, query)).GetProperty("items").EnumerateArray()];

    private async Task<JsonElement> JsonAsync(Guid tenant, string query)
    {
        using var response = await GetAsync(tenant, query);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid tenant, string query, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/verification/items{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, "admin-1"));
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"q-{Guid.NewGuid():N}"[..20];
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
}
