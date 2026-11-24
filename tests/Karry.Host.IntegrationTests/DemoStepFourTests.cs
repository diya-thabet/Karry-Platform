using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Testing;
using Npgsql;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// P2.8 "done when", demo step 4 (plan §9.8): a shift signed on an enrolled tablet goes through sync into review; the
/// controller corrects its end value beyond the four-eyes threshold; another person approves; the ledger keeps the
/// before → after with both hashes, and the entry's new version names the corrector. Every module is the real one.
/// </summary>
[Trait("Story", "US-P2-VER-04")]
public sealed class DemoStepFourTests(SyncHost host) : IClassFixture<SyncHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P2-VER-003")]
    public async Task A_correction_beyond_the_threshold_is_approved_by_a_second_person_and_the_ledger_shows_the_diff()
    {
        var company = await host.CompanyAsync("demo4");
        await SignedEntries.SeedShiftsAsync(host, company);
        var approver = $"approver-{company.Slug}";
        await host.ExecuteAsync(
            """
            INSERT INTO identity.users (id, tenant_id, kind, status, first_name, last_name, email, subject, roles, created_at)
            VALUES (gen_random_uuid(), $1, 'Office', 'Active', 'Second', 'Approver', $2, $3, $4::jsonb, now())
            """, company.Id, $"{approver}@sync.test", approver, $$"""[{"role":"site-manager","siteId":"{{company.North}}"}]""");
        using var tablet = await host.TabletAsync(company);
        var root = Guid.CreateVersion7();
        var startedAt = host.Clock.GetUtcNow().AddHours(2);

        var upload = await tablet.SendAsync(HttpMethod.Post, "/api/v1/sync/batches", new
        {
            commands = new[]
            {
                SignedEntries.Command(tablet, 1, "fieldops.shift_entry.start.v1", 1, company.Operator, new JsonObject
                {
                    ["rootId"] = root.ToString(), ["version"] = 1, ["assetId"] = company.Excavator.ToString(), ["meterId"] = company.Meter.ToString(),
                    ["startedAt"] = SignedEntries.Instant(startedAt), ["startValue"] = "10.0", ["fuelStartPercent"] = 80, ["startPhoto"] = "sha256:" + new string('1', 64),
                }),
                SignedEntries.Command(tablet, 2, "fieldops.shift_entry.end.v1", 2, company.Operator, new JsonObject
                {
                    ["rootId"] = root.ToString(), ["version"] = 2, ["endedAt"] = SignedEntries.Instant(startedAt.AddHours(8)), ["endValue"] = "18.0",
                    ["fuelEndPercent"] = 35, ["endPhoto"] = "sha256:" + new string('2', 64),
                }),
            },
        });
        upload.StatusCode.Should().Be(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync(Ct));

        var queue = await host.SendAsync(HttpMethod.Get, $"/api/v1/verification/items?siteId={company.North}", host.Admin(company));
        queue.StatusCode.Should().Be(HttpStatusCode.OK, await queue.Content.ReadAsStringAsync(Ct));
        var item = (await queue.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("recordId").GetGuid() == root);

        // The photo shows 14.0: the controller corrects the end, Δ moves 4 h (beyond the default 2 h).
        var corrected = await SendAsync(
            $"/api/v1/verification/items/{item.GetProperty("id").GetGuid()}/correct", host.Admin(company), item.GetProperty("version").GetString()!,
            new { fields = new Dictionary<string, string> { ["endValue"] = "14.0" }, reason = "The end photo reads 14.0, the operator typed 18.0." });
        corrected.StatusCode.Should().Be(HttpStatusCode.OK, await corrected.Content.ReadAsStringAsync(Ct));
        var next = (await corrected.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("next");
        var byCorrector = await SendAsync($"/api/v1/verification/items/{next.GetProperty("id").GetGuid()}/second-approve", host.Admin(company), next.GetProperty("version").GetString()!);
        var bySecond = await SendAsync(
            $"/api/v1/verification/items/{next.GetProperty("id").GetGuid()}/second-approve", Recent(company, approver), next.GetProperty("version").GetString()!);

        next.GetProperty("status").GetString().Should().Be("awaiting_second");
        byCorrector.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the corrector never approves their own correction");
        bySecond.StatusCode.Should().Be(HttpStatusCode.OK, await bySecond.Content.ReadAsStringAsync(Ct));
        (await bySecond.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().Should().Be("approved");

        var diff = await RowAsync(
            "SELECT (payload::jsonb->'changes'->0->>'field') || '|' || (payload::jsonb->'changes'->0->>'before') || '|' || (payload::jsonb->'changes'->0->>'after') "
            + "|| '|' || (payload::jsonb->>'fromHash' <> payload::jsonb->>'toHash') || '|' || (payload::jsonb->>'actor') "
            + "FROM audit.ledger_entries WHERE tenant_id = $1 AND event_type = 'verification.review_item_corrected.v1'", company.Id);
        var parts = diff.Split('|');
        (parts[0], decimal.Parse(parts[1], CultureInfo.InvariantCulture), decimal.Parse(parts[2], CultureInfo.InvariantCulture), parts[3], parts[4])
            .Should().Be(("endValue", 18.0m, 14.0m, "true", company.AdminSubject), "the ledger keeps the before → after with both hashes");
        (await RowAsync(
            "SELECT payload::jsonb->>'actor' FROM audit.ledger_entries WHERE tenant_id = $1 AND event_type = 'verification.review_item_second_approved.v1'", company.Id))
            .Should().Be(approver);
        var version = (await RowAsync(
            "SELECT version || '|' || end_value || '|' || corrected_by FROM fieldops.shift_entries WHERE tenant_id = $1 ORDER BY version DESC LIMIT 1", company.Id)).Split('|');
        (int.Parse(version[0], CultureInfo.InvariantCulture), decimal.Parse(version[1], CultureInfo.InvariantCulture), version[2])
            .Should().Be((3, 14.0m, company.AdminSubject), "a correction is a new version naming its author; the signed one stays");
    }

    private string Recent(Company company, string subject) => TestTokens.Create(subject,
        (TenantResolutionMiddleware.TenantClaim, company.Id.ToString()),
        (PermissionMiddleware.AuthTimeClaim, host.Clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

    private async Task<HttpResponseMessage> SendAsync(string path, string token, string version, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("If-Match", version);
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<string> RowAsync(string sql, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(tenant);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }
}
