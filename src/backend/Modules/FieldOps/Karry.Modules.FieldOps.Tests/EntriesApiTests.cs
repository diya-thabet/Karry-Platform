using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>
/// The portal's reading of shift entries (ADR-0034 §7) over HTTP: current versions newest first with filters and pages,
/// an entry with all its versions, meter values with their unit, and only the caller's sites (ADR-0022).
/// </summary>
[Trait("Story", "US-P2-FO-01")]
public sealed class EntriesApiTests(FieldOpsHost host) : IClassFixture<FieldOpsHost>
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Entries_are_listed_as_they_stand_newest_first_page_by_page_with_their_unit_and_usage()
    {
        var tenant = await ProvisionAsync();
        var (site, asset, person) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (first, second, third) = (Id(1), Id(2), Id(3));
        await InsertAsync(tenant, first, 1, site, asset, person, "10.0");
        await InsertAsync(tenant, second, 1, site, asset, person, "100.0");
        await InsertAsync(tenant, second, 2, site, asset, person, "100.0", end: "108.5", flags: ["RULE-FO-02"]);
        await InsertAsync(tenant, third, 1, site, asset, person, "20.0");

        var page = await Json(await GetAsync("/api/v1/fieldops/entries?limit=2", Admin(tenant)));
        var rest = await Json(await GetAsync($"/api/v1/fieldops/entries?limit=2&cursor={second}", Admin(tenant)));

        Ids(page).Should().Equal(third, second);
        page.GetProperty("nextCursor").GetGuid().Should().Be(second);
        Ids(rest).Should().Equal(first);
        rest.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        var closed = page.GetProperty("items")[1];
        (closed.GetProperty("version").GetInt32(), closed.GetProperty("status").GetString(), closed.GetProperty("unit").GetString(), closed.GetProperty("meterKind").GetString())
            .Should().Be((2, "closed", "h", "hours"));
        (closed.GetProperty("startValue").GetDecimal(), closed.GetProperty("endValue").GetDecimal(), closed.GetProperty("usage").GetDecimal()).Should().Be((100.0m, 108.5m, 8.5m));
        closed.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).Should().Equal("RULE-FO-02");
        closed.GetProperty("shift").GetProperty("code").GetString().Should().Be("D");
        closed.GetProperty("shift").GetProperty("date").GetString().Should().Be("2026-11-09");
        page.GetProperty("items")[0].GetProperty("usage").ValueKind.Should().Be(JsonValueKind.Null, "an open entry has no usage yet");
    }

    [Fact]
    public async Task Entries_are_filtered_by_site_shift_machine_operator_status_and_flags()
    {
        var tenant = await ProvisionAsync();
        var (site, otherSite, asset, otherAsset, person, otherPerson) =
            (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (plain, elsewhere, night, flagged) = (Id(11), Id(12), Id(13), Id(14));
        await InsertAsync(tenant, plain, 1, site, asset, person, "1.0");
        await InsertAsync(tenant, elsewhere, 1, otherSite, otherAsset, otherPerson, "1.0");
        await InsertAsync(tenant, night, 1, site, asset, person, "1.0", shiftCode: "N");
        await InsertAsync(tenant, night, 2, site, asset, person, "1.0", end: "2.0", shiftCode: "N");
        await InsertAsync(tenant, flagged, 1, site, asset, otherPerson, "1.0", flags: ["OVERLAP"]);

        async Task<List<Guid>> Find(string query) => Ids(await Json(await GetAsync($"/api/v1/fieldops/entries?{query}", Admin(tenant))));

        (await Find($"siteId={otherSite}")).Should().Equal(elsewhere);
        (await Find($"assetId={asset}")).Should().Equal(flagged, night, plain);
        (await Find($"operatorId={otherPerson}")).Should().Equal(flagged, elsewhere);
        (await Find("shiftDate=2026-11-09&shiftCode=N")).Should().Equal(night);
        (await Find("shiftDate=2026-11-10")).Should().BeEmpty();
        (await Find("status=closed")).Should().Equal(night);
        (await Find("status=open")).Should().Equal(flagged, elsewhere, plain);
        (await Find("flagged=true")).Should().Equal(flagged);
        (await Find("flagged=false")).Should().Equal(night, elsewhere, plain);
    }

    [Fact]
    public async Task An_entry_comes_with_every_version_oldest_first_and_an_unknown_one_is_not_found()
    {
        var tenant = await ProvisionAsync();
        var (site, asset, person, entry) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Id(21));
        await InsertAsync(tenant, entry, 1, site, asset, person, "50.0");
        await InsertAsync(tenant, entry, 2, site, asset, person, "50.0", end: "58.0");

        var detail = await Json(await GetAsync($"/api/v1/fieldops/entries/{entry}", Admin(tenant)));
        var unknown = await GetAsync($"/api/v1/fieldops/entries/{Guid.CreateVersion7()}", Admin(tenant));

        detail.GetProperty("entry").GetProperty("version").GetInt32().Should().Be(2);
        detail.GetProperty("versions").EnumerateArray().Select(v => (v.GetProperty("version").GetInt32(), v.GetProperty("status").GetString()))
            .Should().Equal((1, "open"), (2, "closed"));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(unknown)).GetProperty("code").GetString().Should().Be("fieldops.entry_not_found");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-03")]
    public async Task A_site_manager_reads_the_entries_of_their_site_only_and_an_operator_none()
    {
        var tenant = await ProvisionAsync();
        var (site, otherSite, asset, person) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (mine, theirs) = (Id(31), Id(32));
        await InsertAsync(tenant, mine, 1, site, asset, person, "1.0");
        await InsertAsync(tenant, theirs, 1, otherSite, asset, person, "1.0");
        host.SiteOf[FieldOpsHost.SiteManager] = site;

        var manager = TestTokens.ForTenant(tenant, FieldOpsHost.SiteManager);
        Ids(await Json(await GetAsync("/api/v1/fieldops/entries", manager))).Should().Equal(mine);
        Ids(await Json(await GetAsync($"/api/v1/fieldops/entries?siteId={otherSite}", manager))).Should().BeEmpty();
        (await GetAsync($"/api/v1/fieldops/entries/{theirs}", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound, "outside their sites it does not exist for them");
        (await GetAsync($"/api/v1/fieldops/entries/{mine}", manager)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/api/v1/fieldops/entries", TestTokens.ForTenant(tenant, FieldOpsHost.Operator))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("limit=0", "fieldops.limit_invalid")]
    [InlineData("limit=201", "fieldops.limit_invalid")]
    [InlineData("status=pending", "fieldops.status_invalid")]
    public async Task A_search_with_a_wrong_value_is_refused(string query, string code)
    {
        var tenant = await ProvisionAsync();

        var response = await GetAsync($"/api/v1/fieldops/entries?{query}", Admin(tenant));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be(code);
    }

    /// <summary>Entry IDs in a known order (UUIDv7 from a timestamp): a higher <paramref name="n"/> is newer.</summary>
    private static Guid Id(int n) => Guid.CreateVersion7(Morning.AddSeconds(n));

    private static List<Guid> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    private static string Admin(Guid tenant) => TestTokens.ForTenant(tenant, "admin-1");

    /// <summary>A stored version, as the handlers write it (the handlers themselves are tested in <see cref="ShiftEntryHandlersTests"/>).</summary>
    private async Task InsertAsync(
        Guid tenant, Guid root, int version, Guid site, Guid asset, Guid person, string start, string? end = null, string[]? flags = null, string shiftCode = "D")
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO fieldops.shift_entries (tenant_id, root_id, version, status, site_id, asset_id, meter_id, meter_kind, operator_id, segment_no,
                shift_date, shift_code, shift_starts_at, shift_ends_at, started_at, start_value, fuel_start_percent, start_photo,
                ended_at, end_value, fuel_end_percent, end_photo, stoppages, estimated, flags, envelope_id, command_id, device_id, received_at)
            VALUES ($1, $2, $3, $4, $5, $6, gen_random_uuid(), 'hours', $7, 1,
                '2026-11-09', $8, '2026-11-09T06:00:00Z', '2026-11-09T14:00:00Z', '2026-11-09T06:05:00Z', $9::numeric, 80, 'sha256:' || repeat('a', 64),
                $10, $11::numeric, $12, $13, '[]', false, $14, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), now())
            """, connection);
        insert.Parameters.AddWithValue(tenant);
        insert.Parameters.AddWithValue(root);
        insert.Parameters.AddWithValue(version);
        insert.Parameters.AddWithValue(end is null ? "Open" : "Closed");
        insert.Parameters.AddWithValue(site);
        insert.Parameters.AddWithValue(asset);
        insert.Parameters.AddWithValue(person);
        insert.Parameters.AddWithValue(shiftCode);
        insert.Parameters.AddWithValue(start);
        insert.Parameters.AddWithValue(end is null ? DBNull.Value : new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero));
        insert.Parameters.AddWithValue(end is null ? DBNull.Value : end);
        insert.Parameters.AddWithValue(end is null ? DBNull.Value : 30);
        insert.Parameters.AddWithValue(end is null ? DBNull.Value : "sha256:" + new string('b', 64));
        insert.Parameters.AddWithValue(flags ?? []);
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"f-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
