using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Sales.Tests;

/// <summary>
/// Customers over HTTP (US-P3-SAL-01, ADR-0042): created with their details, changed, blocked with a reason the
/// weighmaster reads and unblocked, every change in the company's ledger; the credit limit masked for roles that may not
/// see it; allowed sites only of the company.
/// </summary>
[Trait("Story", "US-P3-SAL-01")]
public sealed class CustomersApiTests(SalesHost host) : IClassFixture<SalesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Customer(string code = "cli-0042", object? creditLimit = null, Guid[]? sites = null) => new
    {
        code,
        legalName = "  Carrières du Nord SARL ",
        taxId = "tn 1234 567",
        addresses = new[] { new { kind = "billing", line1 = "12 rue du Port", line2 = (string?)null, city = "Bizerte", region = (string?)null, postalCode = "7000", country = "tn" } },
        contacts = new[] { new { name = "Awa Diallo", role = "Achats", phone = "+216 71 000 000", email = "awa@nord.example" } },
        paymentTermsDays = 45,
        basis = "m3-loose",
        allowedSiteIds = sites ?? [],
        creditLimit = creditLimit ?? new { amount = 150000.00m, currency = "tnd" },
    };

    [Fact]
    [Trait("TestCase", "TC-P3-SAL-001")]
    public async Task A_customer_is_created_blocked_with_a_reason_and_unblocked_every_change_ledgered()
    {
        var (tenant, token) = await TenantAsync();

        var created = await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, Customer());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var customer = await Json(created);
        customer.GetProperty("code").GetString().Should().Be("CLI-0042");
        customer.GetProperty("legalName").GetString().Should().Be("Carrières du Nord SARL");
        customer.GetProperty("taxId").GetString().Should().Be("TN1234567", "spaces removed, capitals");
        customer.GetProperty("basis").GetString().Should().Be("m3-loose");
        customer.GetProperty("status").GetString().Should().Be("active");
        customer.GetProperty("creditLimit").GetProperty("amount").GetDecimal().Should().Be(150000m);
        customer.GetProperty("creditLimit").GetProperty("currency").GetString().Should().Be("TND");
        customer.GetProperty("addresses")[0].GetProperty("country").GetString().Should().Be("TN");
        var id = customer.GetProperty("id").GetGuid();

        var refused = await SendAsync(HttpMethod.Post, $"/api/v1/sales/customers/{id}/block", token, new { reason = " " });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(refused)).GetProperty("code").GetString().Should().Be("sales.customer_block_reason_invalid");

        var blocked = await Json(await SendAsync(HttpMethod.Post, $"/api/v1/sales/customers/{id}/block", token, new { reason = "Factures impayées" }));
        blocked.GetProperty("status").GetString().Should().Be("blocked");
        blocked.GetProperty("blockedReason").GetString().Should().Be("Factures impayées");
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/sales/customers?status=blocked", token))).Should().ContainSingle();

        var unblocked = await Json(await SendAsync(HttpMethod.Post, $"/api/v1/sales/customers/{id}/unblock", token));
        unblocked.GetProperty("status").GetString().Should().Be("active");
        unblocked.GetProperty("blockedReason").ValueKind.Should().Be(JsonValueKind.Null);

        (await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, Customer("CLI-0042"))).StatusCode.Should().Be(HttpStatusCode.Conflict, "the code is taken");
        (await LedgerAsync(tenant)).Should().Be(
            "sales.customer_created.v1,sales.customer_credit_limit_set.v1,sales.customer_blocked.v1,sales.customer_unblocked.v1");
    }

    [Fact]
    [Trait("TestCase", "TC-P3-SAL-003")]
    public async Task The_credit_limit_is_masked_for_the_weighmaster_and_read_only_for_finance()
    {
        var (tenant, token) = await TenantAsync();
        var id = (await Json(await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, Customer()))).GetProperty("id").GetGuid();

        var seenByWeighmaster = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sales/customers/{id}", TestTokens.ForTenant(tenant, SalesHost.Weighmaster)));
        seenByWeighmaster.TryGetProperty("creditLimit", out _).Should().BeFalse("the value never leaves the server");
        seenByWeighmaster.GetProperty("maskedFields").EnumerateArray().Select(f => f.GetString()).Should().Contain("creditLimit");
        seenByWeighmaster.GetProperty("legalName").GetString().Should().Be("Carrières du Nord SARL", "the weighmaster needs the rest");

        var seenByFinance = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sales/customers/{id}", TestTokens.ForTenant(tenant, SalesHost.Finance)));
        seenByFinance.GetProperty("creditLimit").GetProperty("amount").GetDecimal().Should().Be(150000m);

        (await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", TestTokens.ForTenant(tenant, SalesHost.Weighmaster), Customer("CLI-0099")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "the weighmaster reads customers, never changes them");
    }

    [Fact]
    public async Task Leaving_the_credit_limit_out_of_a_change_keeps_it_and_an_explicit_null_clears_it()
    {
        var (_, token) = await TenantAsync();
        var created = await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, Customer());
        var id = (await Json(created)).GetProperty("id").GetGuid();

        var change = new { legalName = "Carrières du Nord SA", taxId = "TN1234567", paymentTermsDays = 30, basis = "t" };
        var kept = await Json(await SendAsync(HttpMethod.Put, $"/api/v1/sales/customers/{id}", token, change));
        kept.GetProperty("legalName").GetString().Should().Be("Carrières du Nord SA");
        kept.GetProperty("basis").GetString().Should().Be("t");
        kept.GetProperty("creditLimit").GetProperty("amount").GetDecimal().Should().Be(150000m, "a form without the field must not clear it");

        var cleared = await Json(await SendAsync(HttpMethod.Put, $"/api/v1/sales/customers/{id}", token,
            new { legalName = "Carrières du Nord SA", taxId = "TN1234567", paymentTermsDays = 30, basis = "t", creditLimit = new { amount = (decimal?)null, currency = (string?)null } }));
        cleared.GetProperty("creditLimit").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Allowed_sites_must_be_sites_of_the_company()
    {
        var (tenant, token) = await TenantAsync();
        var site = await SiteAsync(token, "KAK");

        var created = await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, Customer(sites: [site, site]));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(created)).GetProperty("allowedSiteIds").EnumerateArray().Select(s => s.GetGuid()).Should().Equal(site);

        var (_, otherToken) = await TenantAsync();
        var foreignSite = await SiteAsync(otherToken, "OTH");
        var refused = await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, Customer("CLI-0100", sites: [foreignSite]));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(refused)).GetProperty("code").GetString().Should().Be("sales.customer_site_unknown", "another company's site does not exist here");
        tenant.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_change_from_an_old_form_is_refused_with_a_conflict()
    {
        var (_, token) = await TenantAsync();
        var created = await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, Customer());
        var customer = await Json(created);
        var id = customer.GetProperty("id").GetGuid();
        var etag = customer.GetProperty("version").GetString()!;

        var change = new { legalName = "Nouveau nom", paymentTermsDays = 30, basis = "t" };
        (await SendAsync(HttpMethod.Put, $"/api/v1/sales/customers/{id}", token, change, etag)).StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = await SendAsync(HttpMethod.Put, $"/api/v1/sales/customers/{id}", token, new { legalName = "Encore un autre", paymentTermsDays = 30, basis = "t" }, etag);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(stale)).GetProperty("code").GetString().Should().Be("request.version_conflict");
    }

    [Fact]
    public async Task The_desk_finds_a_customer_by_part_of_its_code_name_or_tax_id()
    {
        var (_, token) = await TenantAsync();
        await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, Customer("CLI-0001"));
        await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, new { code = "BTP-7", legalName = "Bâtiments Sud", basis = "t" });

        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/sales/customers?search=nord", token))).Should().ContainSingle();
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/sales/customers?search=btp", token))).Should().ContainSingle();
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/sales/customers?search=1234", token))).Should().ContainSingle("by tax ID");
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/sales/customers?search=%25", token))).Should().BeEmpty("% is a character, not a wildcard");
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/sales/customers", token))).Select(c => c.GetProperty("code").GetString()).Should().Equal("BTP-7", "CLI-0001");
    }

    [Theory]
    [InlineData("code", "", "sales.customer_code_invalid")]
    [InlineData("basis", "m3", "sales.customer_basis_invalid")]
    [InlineData("email", "not-an-address", "sales.customer_contact_invalid")]
    [InlineData("credit", "-5", "sales.customer_credit_limit_invalid")]
    public async Task Invalid_input_is_refused_with_its_code(string field, string value, string code)
    {
        var (_, token) = await TenantAsync();
        object body = field switch
        {
            "code" => new { code = value, legalName = "X", basis = "t" },
            "basis" => new { code = "C-1", legalName = "X", basis = value },
            "email" => new { code = "C-1", legalName = "X", basis = "t", contacts = new[] { new { name = "A", email = value } } },
            _ => new { code = "C-1", legalName = "X", basis = "t", creditLimit = new { amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), currency = "EUR" } },
        };

        var refused = await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, body);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(refused)).GetProperty("code").GetString().Should().Be(code);
    }

    private async Task<Guid> SiteAsync(string token, string code)
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
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<(Guid Tenant, string Token)> TenantAsync()
    {
        var slug = $"s-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await Json(response)).GetProperty("id").GetGuid();
        return (tenant, TestTokens.ForTenant(tenant));
    }

    private Task<string> LedgerAsync(Guid tenant) =>
        ScalarAsync<string>($"SELECT string_agg(event_type, ',' ORDER BY seq) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type LIKE 'sales.%'");

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

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

    private static async Task<IReadOnlyList<JsonElement>> Items(HttpResponseMessage response) =>
        [.. (await Json(response)).GetProperty("items").EnumerateArray()];

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
