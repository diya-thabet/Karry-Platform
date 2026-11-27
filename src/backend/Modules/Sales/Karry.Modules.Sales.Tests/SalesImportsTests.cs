using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Sales.Tests;

/// <summary>
/// Customer and product imports through the Imports pipeline (ADR-0027): errors on their cells block the whole file
/// (nothing half-imported, TC-P3-SAL-004); the fixed file creates everything in one transaction; the same file again
/// changes nothing; a known code is changed and empty optional cells keep what is there.
/// </summary>
[Trait("Story", "US-P3-SAL-01")]
public sealed class SalesImportsTests(SalesHost host) : IClassFixture<SalesHost>
{
    private static readonly string[] CustomerHeaders =
    [
        "Code", "Legal name", "Tax ID", "Payment terms (days)", "Invoicing basis", "Billing address", "City", "Country", "Contact name",
        "Contact phone", "Contact e-mail", "Allowed sites (codes)",
    ];

    private static readonly string[] ProductHeaders = ["Code", "Name", "Material (code)", "Sale unit"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ImportDriver Imports => new(host.Client, host.RunImportsAsync);

    private static string?[] Customer(int n, string? sites = null) =>
        [$"CLI-{n:D4}", $"Client {n} SARL", $"TN{n:D6}", "30", "t", $"{n} rue du Port", "Bizerte", "TN", $"Contact {n}", null, $"achats{n}@client.example", sites];

    [Fact]
    [Trait("TestCase", "TC-P3-SAL-004")]
    public async Task Two_hundred_customers_with_bad_rows_import_nothing_and_the_fixed_file_imports_all_at_once()
    {
        var (tenant, token) = await TenantAsync();
        await SiteAsync(token, "NORD");
        var rows = Enumerable.Range(1, 200).Select(n => Customer(n, n % 2 == 0 ? "nord" : null)).ToList();
        var broken = rows.ToList();
        broken[9] = [.. rows[9].Take(3), "beaucoup", .. rows[9].Skip(4)];             // row 11: terms not a number
        broken[49] = [.. rows[49].Take(4), "kg", .. rows[49].Skip(5)];                // row 51: unknown basis
        broken[99] = [.. rows[99].Take(10), "pas-un-mail", .. rows[99].Skip(11)];     // row 101: bad e-mail
        broken[149] = [.. rows[149].Take(11), "NORD;SUD"];                            // row 151: unknown site
        broken[199] = [.. rows[0]];                                                    // row 201: same code as row 2

        var job = await Imports.ImportAsync(tenant, "customers", Workbook(CustomerHeaders, broken), Ct);

        (await Imports.IssuesAsync(tenant, job, "error", Ct)).Should().BeEquivalentTo([
            (11, "D11", "imports.number_invalid"),
            (51, "E51", "sales.customer_basis_invalid"),
            (101, "I101", "sales.customer_contact_invalid"),
            (151, "L151", "sales.customer_site_unknown"),
            (2, "A2", "imports.key_duplicate"),
            (201, "A201", "imports.key_duplicate"),
        ]);
        (await Imports.RequestCommitAsync(tenant, job, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await CountAsync(tenant, "customers")).Should().Be(0, "nothing is half-imported");

        var done = await Imports.ImportAndCommitAsync(tenant, "customers", Workbook(CustomerHeaders, rows), Ct);
        done.GetProperty("created").GetInt32().Should().Be(200);
        (await CountAsync(tenant, "customers")).Should().Be(200);
        var second = (await Json(await SendAsync(HttpMethod.Get, "/api/v1/sales/customers?search=CLI-0002", token))).GetProperty("items")[0];
        second.GetProperty("allowedSiteIds").GetArrayLength().Should().Be(1);
        second.GetProperty("contacts")[0].GetProperty("email").GetString().Should().Be("achats2@client.example");

        var again = await Imports.CommitAsync(tenant, await Imports.ImportAsync(tenant, "customers", Workbook(CustomerHeaders, rows), Ct), Ct);
        (again.GetProperty("created").GetInt32(), again.GetProperty("unchanged").GetInt32()).Should().Be((0, 200));
        (await LedgerCountAsync(tenant, "sales.customer_created.v1")).Should().Be(200);
    }

    [Fact]
    public async Task A_known_customer_is_changed_and_empty_optional_cells_keep_what_it_has()
    {
        var (tenant, token) = await TenantAsync();
        await Imports.ImportAndCommitAsync(tenant, "customers", Workbook(CustomerHeaders, [Customer(1)]), Ct);

        // Only the code, name and terms: the address, contact and basis stay.
        string?[] renamed = ["cli-0001", "Client Un SA", null, "60", null, null, null, null, null, null, null, null];
        var changed = await Imports.ImportAndCommitAsync(tenant, "customers", Workbook(CustomerHeaders, [renamed]), Ct);
        changed.GetProperty("updated").GetInt32().Should().Be(1);

        var customer = (await Json(await SendAsync(HttpMethod.Get, "/api/v1/sales/customers?search=CLI-0001", token))).GetProperty("items")[0];
        customer.GetProperty("legalName").GetString().Should().Be("Client Un SA");
        customer.GetProperty("paymentTermsDays").GetInt32().Should().Be(60);
        customer.GetProperty("taxId").ValueKind.Should().Be(JsonValueKind.Null, "the tax ID is part of the row: empty clears it");
        customer.GetProperty("addresses")[0].GetProperty("city").GetString().Should().Be("Bizerte");
        customer.GetProperty("contacts").GetArrayLength().Should().Be(1);
        (await LedgerCountAsync(tenant, "sales.customer_changed.v1")).Should().Be(1);
    }

    [Fact]
    [Trait("Story", "US-P3-SAL-02")]
    public async Task Products_import_against_sold_fractions_and_an_archived_product_is_refused()
    {
        var (tenant, token) = await TenantAsync();
        await MaterialAsync(token, "MAT-816", "product");
        await MaterialAsync(token, "ROCK", "raw-rock");

        var job = await Imports.ImportAsync(tenant, "products", Workbook(ProductHeaders, [
            ["g-8/16", "Gravier 8/16", "mat-816", "m3-loose"],
            ["BRUT", "Tout-venant", "ROCK", null],
            ["SAB", "Sable", "INCONNU", null],
            ["G-4/8", "Gravier 4/8", "MAT-816", "sac"],
        ]), Ct);
        (await Imports.IssuesAsync(tenant, job, "error", Ct)).Should().BeEquivalentTo([
            (3, "C3", "sales.product_material_not_sold"),
            (4, "C4", "sales.product_material_not_sold"),
            (5, "D5", "sales.product_sale_unit_invalid"),
        ]);
        (await CountAsync(tenant, "products")).Should().Be(0);

        var done = await Imports.ImportAndCommitAsync(tenant, "products", Workbook(ProductHeaders, [["g-8/16", "Gravier 8/16", "mat-816", "m3-loose"]]), Ct);
        done.GetProperty("created").GetInt32().Should().Be(1);
        var product = (await Json(await SendAsync(HttpMethod.Get, "/api/v1/sales/products", token))).GetProperty("items")[0];
        product.GetProperty("code").GetString().Should().Be("G-8/16");
        product.GetProperty("saleUnit").GetString().Should().Be("m3-loose");

        (await SendAsync(HttpMethod.Post, $"/api/v1/sales/products/{product.GetProperty("id").GetGuid()}/archive", token)).IsSuccessStatusCode.Should().BeTrue();
        var archived = await Imports.ImportAsync(tenant, "products", Workbook(ProductHeaders, [["G-8/16", "Gravier lavé", "MAT-816", null]]), Ct);
        (await Imports.IssuesAsync(tenant, archived, "error", Ct)).Should().ContainSingle().Which.Should().Be((2, "A2", "sales.product_archived"));
    }

    private static byte[] Workbook(string[] headers, List<string?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Data");
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        for (var r = 0; r < rows.Count; r++)
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

    private async Task MaterialAsync(string token, string code, string kind)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/materials", token, new
        {
            code,
            name = $"Matériau {code}",
            kind,
            densities = new { loose = 1.50m, compacted = 1.75m, moisture = 0.02m },
            effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task SiteAsync(string token, string code)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, new
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
    }

    private async Task<(Guid Tenant, string Token)> TenantAsync()
    {
        var slug = $"m-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await Json(response)).GetProperty("id").GetGuid();
        return (tenant, TestTokens.ForTenant(tenant, "admin-1"));
    }

    private Task<long> CountAsync(Guid tenant, string table) => ScalarAsync<long>($"SELECT count(*) FROM sales.{table} WHERE tenant_id = '{tenant}'");

    private Task<long> LedgerCountAsync(Guid tenant, string eventType) =>
        ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = '{eventType}'");

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
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

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
}
