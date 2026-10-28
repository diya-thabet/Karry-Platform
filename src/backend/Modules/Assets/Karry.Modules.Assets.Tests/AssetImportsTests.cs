using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Application;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Assets.Tests;

/// <summary>
/// The asset and meter imports through the Imports pipeline (US-P1-ORG-03, ADR-0027): a fleet file with errors is
/// reported cell by cell and never committed (TC-P1-ORG-001), 1,000 assets commit in under a minute (TC-P1-ORG-004), the
/// same file again changes nothing (TC-P1-ORG-002), and meter rows become readings that never go back.
/// </summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class AssetImportsTests(AssetsHost host) : IClassFixture<AssetsHost>
{
    private static readonly string[] AssetHeaders = ["Code", "Nom", "Type", "Site", "Marque", "Modèle", "N° de série", "Immatriculation", "Année", "Date d'acquisition"];

    private static readonly string[] MeterHeaders = ["Asset code", "Meter", "Reading", "Read on"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fleet_of_1000_with_20_bad_rows_is_reported_cell_by_cell_never_committed_and_once_fixed_commits_in_under_a_minute()
    {
        var tenant = await ProvisionAsync();
        await SiteAsync(tenant, "CAR-NORD");
        await SiteAsync(tenant, "CAR-SUD");
        var rows = Enumerable.Range(1, 1000).Select(i => Fleet(i)).ToList();
        var broken = rows.Select((row, index) => index % 50 == 7 ? Break(row, index / 50) : row).ToList();

        var stopwatch = Stopwatch.StartNew();
        var job = await ImportAsync(tenant, "assets", Workbook(AssetHeaders, broken));
        var report = await JobAsync(tenant, job);
        (report.GetProperty("status").GetString(), report.GetProperty("rows").GetInt32()).Should().Be(("validated", 1000));
        var errors = await IssuesAsync(tenant, job, "error");
        errors.Select(e => e.Row).Distinct().Should().HaveCount(20, "20 rows are wrong, each reported");
        errors.Should().Contain([
            (9, "A9", "assets.code_invalid"),
            (59, "B59", "imports.value_required"),
            (109, "C109", "assets.type_invalid"),
            (159, "D159", "assets.site_invalid"),
            (209, "I209", "imports.number_invalid"),
            (259, "J259", "imports.date_invalid"),
            (309, "I309", "assets.year_invalid"),
        ]);
        (await PostAsync(tenant, $"/api/v1/imports/{job}/commit")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await UsageAsync(tenant)).Should().Be(0, "nothing is committed while one error remains");

        var fixedJob = await ImportAsync(tenant, "assets", Workbook(AssetHeaders, rows));
        (await JobAsync(tenant, fixedJob)).GetProperty("errors").GetInt32().Should().Be(0);
        (await PostAsync(tenant, $"/api/v1/imports/{fixedJob}/commit")).StatusCode.Should().Be(HttpStatusCode.OK);
        await host.RunImportsAsync();
        stopwatch.Stop();

        var committed = await JobAsync(tenant, fixedJob);
        (committed.GetProperty("status").GetString(), committed.GetProperty("created").GetInt32()).Should().Be(("committed", 1000));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "TC-P1-ORG-004: both files validated and the fleet committed");
        (await UsageAsync(tenant)).Should().Be(1000);
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'assets.asset_registered.v1'")).Should().Be(1000);
        (await ScalarAsync<long>($"SELECT count(*) FROM assets.meters WHERE tenant_id = '{tenant}'")).Should().Be(1000 + 100, "the types' default meters: 100 haul trucks have hours and km");
    }

    [Fact]
    public async Task The_demo_scenario_reports_each_planted_mistake_on_its_cell_then_the_fixed_file_commits_the_60_machines()
    {
        var tenant = await ProvisionAsync();
        await SiteAsync(tenant, "CAR-NORD");
        await SiteAsync(tenant, "LAV-SUD");
        string?[][] File(bool withMistakes) => [.. DemoFleetFile.Rows(withMistakes).Select(r => r.ToArray())];
        var headers = DemoFleetFile.Headers.ToArray();

        var job = await ImportAsync(tenant, "assets", Workbook(headers, File(withMistakes: true)));
        (await IssuesAsync(tenant, job, "error")).Select(i => (i.Row, i.Cell, i.Code))
            .Should().Equal(DemoFleetFile.ExpectedIssues.Select(i => (i.Row, (string?)i.Cell, (string?)i.Code)), "every planted mistake, and nothing else");
        (await IssuesAsync(tenant, job, "warning")).Should().BeEmpty();
        (await PostAsync(tenant, $"/api/v1/imports/{job}/commit")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var done = await CommitAsync(tenant, "assets", Workbook(headers, File(withMistakes: false)));
        (done.GetProperty("created").GetInt32(), done.GetProperty("rows").GetInt32()).Should().Be((60, 60));
        (await ScalarAsync<long>($"SELECT count(*) FROM assets.assets a JOIN organization.sites s ON s.id = a.site_id WHERE a.tenant_id = '{tenant}' AND s.code = 'LAV-SUD'"))
            .Should().Be(14);
    }

    [Fact]
    public async Task The_same_file_again_changes_nothing_and_a_changed_row_changes_or_moves_its_asset()
    {
        var tenant = await ProvisionAsync();
        await SiteAsync(tenant, "CAR-NORD");
        await SiteAsync(tenant, "CAR-SUD");
        var rows = Enumerable.Range(1, 5).Select(i => Fleet(i)).ToList();
        await CommitAsync(tenant, "assets", Workbook(AssetHeaders, rows));

        var again = await ImportAsync(tenant, "assets", Workbook(AssetHeaders, rows));
        (await PreviewAsync(tenant, again)).Should().AllSatisfy(r => r.Action.Should().Be("unchanged"));
        await PostAsync(tenant, $"/api/v1/imports/{again}/commit");
        await host.RunImportsAsync();
        var repeated = await JobAsync(tenant, again);
        (repeated.GetProperty("created").GetInt32(), repeated.GetProperty("unchanged").GetInt32()).Should().Be((0, 5));
        (await UsageAsync(tenant)).Should().Be(5, "TC-P1-ORG-002: no duplicates");

        rows[1] = rows[1] with { Name = "Pelle renommée" };
        rows[2] = rows[2] with { Site = "car-nord" }; // PEL-0003 was on CAR-SUD; codes match without case
        var changed = await ImportAsync(tenant, "assets", Workbook(AssetHeaders, rows));
        (await PreviewAsync(tenant, changed)).Select(r => r.Action).Should().Equal("unchanged", "update", "update", "unchanged", "unchanged");
        await PostAsync(tenant, $"/api/v1/imports/{changed}/commit");
        await host.RunImportsAsync();

        (await JobAsync(tenant, changed)).GetProperty("updated").GetInt32().Should().Be(2);

        var twice = await ImportAsync(tenant, "assets", Workbook(AssetHeaders, [Fleet(7), Fleet(8), Fleet(7) with { Code = "pel-0007" }]));
        (await IssuesAsync(tenant, twice, "error")).Should().Equal((2, "A2", "imports.key_duplicate"), (4, "A4", "imports.key_duplicate"));
        (await PreviewAsync(tenant, twice)).Should().Equal(("PEL-0008", "create"));
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'assets.asset_changed.v1'")).Should().Be(1);
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'assets.asset_moved.v1'")).Should().Be(1);
    }

    [Fact]
    public async Task A_site_archived_between_the_preview_and_the_commit_stops_the_whole_file()
    {
        var tenant = await ProvisionAsync();
        await SiteAsync(tenant, "CAR-SUD");
        var north = await SiteAsync(tenant, "CAR-NORD");
        var job = await ImportAsync(tenant, "assets", Workbook(AssetHeaders, [.. Enumerable.Range(1, 4).Select(i => Fleet(i))]));
        (await JobAsync(tenant, job)).GetProperty("errors").GetInt32().Should().Be(0);
        (await PostAsync(tenant, $"/api/v1/imports/{job}/commit")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await PostAsync(tenant, $"/api/v1/organization/sites/{north}/archive")).IsSuccessStatusCode.Should().BeTrue();
        await host.RunImportsAsync();

        var after = await JobAsync(tenant, job);
        (after.GetProperty("status").GetString(), after.GetProperty("errors").GetInt32()).Should().Be(("validated", 2));
        (await IssuesAsync(tenant, job, "error")).Select(i => i.Code).Should().AllBe("assets.site_invalid");
        (await UsageAsync(tenant)).Should().Be(0, "the rows on the other site are not written either");
    }

    [Fact]
    public async Task Meter_rows_add_missing_meters_and_record_readings_that_never_go_back()
    {
        var tenant = await ProvisionAsync();
        await SiteAsync(tenant, "CAR-NORD");
        await SiteAsync(tenant, "CAR-SUD");
        await CommitAsync(tenant, "assets", Workbook(AssetHeaders, [Fleet(1), Fleet(2)]));
        var yesterday = DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var file = Workbook(MeterHeaders,
        [
            ["PEL-0001", "hours", "4250.5", yesterday],
            ["pel-0001", "km", "120", null],
            ["PEL-0002", "hours", "-3", null],
            ["PEL-9999", "hours", "10", null],
            ["PEL-0002", "litres", "10", null],
        ]);
        var job = await ImportAsync(tenant, "meters", file);
        (await IssuesAsync(tenant, job, "error")).Should().Equal(
            (4, "C4", "assets.reading_invalid"),
            (5, "A5", "assets.asset_not_found"),
            (6, "B6", "assets.meter_kind_invalid"));
        (await PreviewAsync(tenant, job)).Should().Equal(("PEL-0001/hours", "update"), ("PEL-0001/km", "create"));

        var clean = await CommitAsync(tenant, "meters", Workbook(MeterHeaders, [["PEL-0001", "hours", "4250.5", yesterday], ["PEL-0001", "km", "120", null]]));
        (clean.GetProperty("created").GetInt32(), clean.GetProperty("updated").GetInt32()).Should().Be((1, 1));
        (await ScalarAsync<decimal>($"SELECT last_value FROM assets.meters WHERE tenant_id = '{tenant}' AND kind = 'Hours' AND asset_id = (SELECT id FROM assets.assets WHERE tenant_id = '{tenant}' AND code = 'PEL-0001')"))
            .Should().Be(4250.5m);

        var backwards = await ImportAsync(tenant, "meters", Workbook(MeterHeaders, [["PEL-0001", "hours", "4250.5", null], ["PEL-0001", "km", "100", null]]));
        (await PreviewAsync(tenant, backwards)).Should().Equal(("PEL-0001/hours", "unchanged"));
        (await IssuesAsync(tenant, backwards, "error")).Should().Equal((3, "C3", "assets.reading_decreased"));
    }

    [Fact]
    public async Task Templates_list_only_the_companys_own_active_sites_and_new_assets_follow_the_plan_limit()
    {
        var tenant = await ProvisionAsync();
        var other = await ProvisionAsync();
        await SiteAsync(tenant, "CAR-NORD");
        await SiteAsync(other, "AUTRE-SITE");

        using var template = await SendAsync(HttpMethod.Get, "/api/v1/imports/templates/assets?language=fr", tenant);
        template.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var workbook = new XLWorkbook(await template.Content.ReadAsStreamAsync(Ct)))
        {
            workbook.Worksheet(1).Row(1).CellsUsed().Select(c => c.GetText()).Should().Equal(AssetHeaders.Select((h, i) => i < 4 ? h + " *" : h));
            var lists = workbook.Worksheet("Listes");
            var sites = lists.Column(lists.Row(1).CellsUsed().Single(c => c.GetText() == "sites").Address.ColumnNumber).CellsUsed().Skip(1).Select(c => c.GetText());
            sites.Should().ContainSingle("another company's sites are never listed").Which.Should().Be("CAR-NORD");
        }

        host.Limits.ByTenant[tenant] = 2; // the grace allows 3
        var job = await ImportAsync(tenant, "assets", Workbook(AssetHeaders, [.. Enumerable.Range(1, 4).Select(i => Fleet(i) with { Site = "CAR-NORD" })]));
        var issues = await IssuesAsync(tenant, job, "error");
        issues.Should().Equal((0, null, "billing.limit_reached"));
    }

    private static FleetRow Fleet(int i) => new(
        $"PEL-{i:0000}", $"Engin {i}", i % 10 == 0 ? "haul-truck" : "excavator", i % 2 == 0 ? "CAR-NORD" : "CAR-SUD", "Caterpillar", "336",
        $"SN{i:000000}", null, (2010 + (i % 12)).ToString(CultureInfo.InvariantCulture), "2019-03-15");

    /// <summary>One of seven mistakes, in turn (rows 9, 59, 109, ... of the sheet).</summary>
    private static FleetRow Break(FleetRow row, int which) => (which % 7) switch
    {
        0 => row with { Code = "pel 0001!" },
        1 => row with { Name = null },
        2 => row with { Type = "rocket" },
        3 => row with { Site = "NOWHERE" },
        4 => row with { Year = "deux mille" },
        5 => row with { AcquiredOn = "31/02/2020" },
        _ => row with { Year = "1890" },
    };

    private sealed record FleetRow(string? Code, string? Name, string? Type, string? Site, string? Make, string? Model, string? Serial, string? Plate, string? Year, string? AcquiredOn)
    {
        public string?[] Cells => [Code, Name, Type, Site, Make, Model, Serial, Plate, Year, AcquiredOn];
    }

    private static byte[] Workbook(string[] headers, IEnumerable<FleetRow> rows) => Workbook(headers, rows.Select(r => r.Cells).ToArray());

    private static byte[] Workbook(string[] headers, string?[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Données");
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] is { } value)
                {
                    sheet.Cell(r + 2, c + 1).Value = value;
                }
            }
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private ImportDriver Imports => new(host.Client, host.RunImportsAsync);

    /// <summary>Uploads and validates; returns the job.</summary>
    private Task<Guid> ImportAsync(Guid tenant, string kind, byte[] file) => Imports.ImportAsync(tenant, kind, file, Ct);

    /// <summary>Uploads, validates (no error expected) and commits; returns the committed job.</summary>
    private Task<JsonElement> CommitAsync(Guid tenant, string kind, byte[] file) => Imports.ImportAndCommitAsync(tenant, kind, file, Ct);

    private Task<JsonElement> JobAsync(Guid tenant, Guid job) => Imports.JobAsync(tenant, job, Ct);

    private Task<IReadOnlyList<(int Row, string? Cell, string? Code)>> IssuesAsync(Guid tenant, Guid job, string severity) => Imports.IssuesAsync(tenant, job, severity, Ct);

    private Task<IReadOnlyList<(string? Key, string? Action)>> PreviewAsync(Guid tenant, Guid job) => Imports.PreviewAsync(tenant, job, Ct);

    private Task<HttpResponseMessage> PostAsync(Guid tenant, string path) => SendAsync(HttpMethod.Post, path, tenant);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? tenant, object? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method != HttpMethod.Get)
        {
            request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForTenant(tenant!.Value, "admin-1"));
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> SiteAsync(Guid tenant, string code)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", tenant, new
        {
            code,
            name = $"Carrière {code}",
            type = "quarry",
            address = new { line = "Route de Bizerte km 12", city = "Tunis", region = (string?)null, postalCode = "1000", country = "TN" },
            location = new { latitude = 36.8045, longitude = 10.0056 },
            boundary = (object?)null,
            timeZone = "Africa/Tunis",
            currency = (string?)null,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"a-{Guid.NewGuid():N}"[..20];
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", null, new { slug, name = $"Carrière {slug}" }, TestTokens.ForPlatformOperator());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<long> UsageAsync(Guid tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        return await scope.ServiceProvider.GetServices<IUsageSource>().Single(s => s.Metric == Metrics.Assets).CountAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
