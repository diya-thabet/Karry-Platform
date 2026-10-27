using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Organization.Tests;

/// <summary>
/// The site import through the Imports pipeline (US-P1-ORG-03, ADR-0027): errors are reported on their cells and block
/// the whole file, the same file again changes nothing (TC-P1-ORG-002), a changed row changes its site and keeps its
/// boundary, an archived site is refused, and the site limit counts the file's creations.
/// </summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class SiteImportsTests(OrganizationHost host) : IClassFixture<OrganizationHost>
{
    private static readonly string[] Headers = ["Code", "Nom", "Type", "Adresse", "Ville", "Gouvernorat / région", "Code postal", "Pays", "Latitude", "Longitude", "Fuseau horaire", "Devise"];

    private static readonly double[][] Triangle = [[10.14, 36.40], [10.15, 36.40], [10.15, 36.41], [10.14, 36.40]];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ImportDriver Imports => new(host.Client, host.RunImportsAsync);

    [Fact]
    public async Task Errors_are_reported_on_their_cells_and_block_the_file_and_the_fixed_file_creates_the_sites()
    {
        var tenant = await ProvisionAsync();
        Row[] rows =
        [
            Site("car-nord"),
            Site("CAR-SUD") with { Latitude = "abc" },
            Site("CAR-EST") with { Latitude = "95" },
            Site("CAR-OUEST") with { Country = "XX" },
            Site("SAB-01") with { Type = "beach" },
            Site("SAB-02") with { TimeZone = "Mars/Olympus" },
            Site("SAB-03") with { Name = null },
            Site("CAR-NORD"),
        ];

        var job = await Imports.ImportAsync(tenant, "sites", Workbook(rows), Ct);

        (await Imports.IssuesAsync(tenant, job, "error", Ct)).Should().BeEquivalentTo([
            (2, "A2", "imports.key_duplicate"),
            (3, "I3", "imports.number_invalid"),
            (4, "I4", "organization.site_location_invalid"),
            (5, "D5", "organization.site_address_invalid"),
            (6, "C6", "organization.site_type_invalid"),
            (7, "K7", "organization.time_zone_unknown"),
            (8, "B8", "imports.value_required"),
            (9, "A9", "imports.key_duplicate"),
        ]);
        (await Imports.RequestCommitAsync(tenant, job, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await CountAsync(tenant)).Should().Be(0);

        var good = await Imports.ImportAndCommitAsync(tenant, "sites", Workbook(Site("CAR-NORD"), Site("CAR-SUD") with { TimeZone = null, Currency = "eur" }), Ct);

        good.GetProperty("created").GetInt32().Should().Be(2);
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'organization.site_created.v1'")).Should().Be(2);
        (await ScalarAsync<string>($"SELECT time_zone || '/' || currency FROM organization.sites WHERE tenant_id = '{tenant}' AND code = 'CAR-SUD'"))
            .Should().Be(await ScalarAsync<string>($"SELECT time_zone FROM tenancy.tenants WHERE id = '{tenant}'") + "/EUR", "an empty time zone is the company's");
    }

    [Fact]
    public async Task The_same_file_again_changes_nothing_a_changed_row_keeps_the_boundary_and_an_archived_site_is_refused()
    {
        var tenant = await ProvisionAsync();
        var drawn = await CreateWithBoundaryAsync(tenant, "CAR-NORD");
        Row[] rows = [Site("CAR-NORD"), Site("CAR-SUD"), Site("SAB-01")];
        await Imports.ImportAndCommitAsync(tenant, "sites", Workbook(rows), Ct);

        var again = await Imports.ImportAsync(tenant, "sites", Workbook(rows), Ct);
        (await Imports.PreviewAsync(tenant, again, Ct)).Select(r => r.Action).Should().AllBe("unchanged", "TC-P1-ORG-002");
        var repeated = await Imports.CommitAsync(tenant, again, Ct);
        (repeated.GetProperty("created").GetInt32(), repeated.GetProperty("unchanged").GetInt32()).Should().Be((0, 3));
        (await CountAsync(tenant)).Should().Be(3);

        rows[0] = rows[0] with { Name = "Carrière Nord renommée" };
        var renamed = await Imports.ImportAndCommitAsync(tenant, "sites", Workbook(rows), Ct);
        renamed.GetProperty("updated").GetInt32().Should().Be(1);
        var site = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{drawn}", tenant));
        site.GetProperty("name").GetString().Should().Be("Carrière Nord renommée");
        site.GetProperty("boundary").ValueKind.Should().Be(JsonValueKind.Object, "the template carries no boundary, so the drawn one stays");

        (await SendAsync(HttpMethod.Post, $"/api/v1/organization/sites/{drawn}/archive", tenant)).IsSuccessStatusCode.Should().BeTrue();
        var archived = await Imports.ImportAsync(tenant, "sites", Workbook(rows), Ct);
        (await Imports.IssuesAsync(tenant, archived, "error", Ct)).Should().ContainSingle().Which.Should().Be((2, "A2", "organization.site_archived"));
    }

    [Fact]
    public async Task The_site_limit_counts_every_site_the_file_would_create()
    {
        var tenant = await ProvisionAsync();
        host.Limits.ByTenant[tenant] = 2; // blocked from 3 sites on (2 + 10 % rounded up)

        var tooMany = await Imports.ImportAsync(tenant, "sites", Workbook(Site("S-01"), Site("S-02"), Site("S-03"), Site("S-04")), Ct);
        (await Imports.IssuesAsync(tenant, tooMany, "error", Ct)).Should().ContainSingle().Which.Should().Be((0, null, "billing.limit_reached"));

        (await Imports.ImportAndCommitAsync(tenant, "sites", Workbook(Site("S-01"), Site("S-02"), Site("S-03")), Ct)).GetProperty("created").GetInt32().Should().Be(3);
    }

    private static Row Site(string code) =>
        new(code, $"Carrière {code}", "quarry", "Route de Zaghouan km 12", "Zaghouan", null, "1100", "TN", "36.4029", "10.1429", "Africa/Tunis", null);

    private sealed record Row(
        string? Code, string? Name, string? Type, string? Address, string? City, string? Region, string? PostalCode, string? Country,
        string? Latitude, string? Longitude, string? TimeZone, string? Currency)
    {
        public string?[] Cells => [Code, Name, Type, Address, City, Region, PostalCode, Country, Latitude, Longitude, TimeZone, Currency];
    }

    private static byte[] Workbook(params Row[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Données");
        for (var c = 0; c < Headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = Headers[c];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            var cells = rows[r].Cells;
            for (var c = 0; c < cells.Length; c++)
            {
                if (cells[c] is { } value)
                {
                    sheet.Cell(r + 2, c + 1).Value = value;
                }
            }
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private async Task<Guid> CreateWithBoundaryAsync(Guid tenant, string code)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", tenant, new
        {
            code,
            name = $"Carrière {code}",
            type = "quarry",
            address = new { line = "Route de Zaghouan km 12", city = "Zaghouan", region = (string?)null, postalCode = "1100", country = "TN" },
            location = new { latitude = 36.4029, longitude = 10.1429 },
            boundary = new[] { Triangle },
            timeZone = "Africa/Tunis",
            currency = (string?)null,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private Task<long> CountAsync(Guid tenant) => ScalarAsync<long>($"SELECT count(*) FROM organization.sites WHERE tenant_id = '{tenant}'");

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"i-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", null, new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? tenant, object? body = null)
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

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenant is { } t ? TestTokens.ForTenant(t, "admin-1") : TestTokens.ForPlatformOperator());
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
