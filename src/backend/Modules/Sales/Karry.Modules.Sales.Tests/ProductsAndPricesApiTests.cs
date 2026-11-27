using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Sales.Tests;

/// <summary>
/// Products and price lists over HTTP (US-P3-SAL-02, ADR-0042): a product is a sold fraction of the company; versions
/// per site and per customer from a date on the company's clock; the price on a date answered by one call; unit prices
/// masked for roles that may not read them.
/// </summary>
[Trait("Story", "US-P3-SAL-02")]
public sealed class ProductsAndPricesApiTests(SalesHost host) : IClassFixture<SalesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The tenant's clock is UTC (the default): its today is the UTC date.
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Day(int days) => Today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string At(int days) => Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(days).ToString("O", CultureInfo.InvariantCulture));

    private static object PriceList(Guid site, Guid? customer, int startsInDays, params (Guid Product, decimal Price)[] lines) => new
    {
        siteId = site,
        customerId = customer,
        currency = "tnd",
        effectiveFrom = Day(startsInDays),
        lines = lines.Select(l => new { productId = l.Product, unitPrice = l.Price }).ToArray(),
    };

    [Fact]
    [Trait("TestCase", "TC-P3-SAL-002")]
    public async Task The_price_on_a_date_comes_from_the_customer_version_then_the_site_version_then_none()
    {
        var (tenant, token) = await TenantAsync();
        var site = await SiteAsync(token, "NORD");
        var gravel = await ProductAsync(token, "G-8/16", await MaterialAsync(token, "MAT-816", "product"));
        var sand = await ProductAsync(token, "SAB-0/4", await MaterialAsync(token, "MAT-04", "product"));
        var customer = await CustomerAsync(token);

        (await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", token, PriceList(site, null, 0, (gravel, 30m)))).StatusCode.Should().Be(HttpStatusCode.Created);
        var own = await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", token, PriceList(site, customer, 3, (gravel, 27.5m)));
        own.StatusCode.Should().Be(HttpStatusCode.Created);
        var ownList = (await Json(own)).GetProperty("id").GetGuid();

        var beforeOwn = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sales/prices?siteId={site}&productId={gravel}&customerId={customer}&at={At(1)}", token));
        beforeOwn.GetProperty("unitPrice").GetDecimal().Should().Be(30m);
        beforeOwn.GetProperty("source").GetString().Should().Be("site");
        beforeOwn.GetProperty("currency").GetString().Should().Be("TND");
        beforeOwn.GetProperty("saleUnit").GetString().Should().Be("t");

        var withOwn = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sales/prices?siteId={site}&productId={gravel}&customerId={customer}&at={At(3)}", token));
        withOwn.GetProperty("unitPrice").GetDecimal().Should().Be(27.5m);
        withOwn.GetProperty("source").GetString().Should().Be("customer");
        withOwn.GetProperty("priceListId").GetGuid().Should().Be(ownList);
        withOwn.GetProperty("effectiveFrom").GetString().Should().Be(Day(3));

        var none = await SendAsync(HttpMethod.Get, $"/api/v1/sales/prices?siteId={site}&productId={sand}&customerId={customer}", token);
        none.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(none)).GetProperty("code").GetString().Should().Be("sales.no_price");

        (await LedgerAsync(tenant)).Should().Be(
            "sales.product_created.v1,sales.product_created.v1,sales.customer_created.v1,sales.price_list_created.v1,sales.price_list_created.v1");
    }

    [Fact]
    public async Task A_version_changes_or_is_withdrawn_only_before_it_comes_into_force()
    {
        var (_, token) = await TenantAsync();
        var site = await SiteAsync(token, "SUD");
        var gravel = await ProductAsync(token, "G-8/16", await MaterialAsync(token, "MAT-816", "product"));

        var current = await Json(await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", token, PriceList(site, null, 0, (gravel, 30m))));
        current.GetProperty("inForce").GetBoolean().Should().BeTrue();
        var inForce = await SendAsync(HttpMethod.Put, $"/api/v1/sales/price-lists/{current.GetProperty("id").GetGuid()}/lines", token,
            new { lines = new[] { new { productId = gravel, unitPrice = 31m } } });
        inForce.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(inForce)).GetProperty("code").GetString().Should().Be("sales.price_list_in_force");

        var next = await Json(await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", token, PriceList(site, null, 7, (gravel, 33m))));
        next.GetProperty("inForce").GetBoolean().Should().BeFalse();
        var nextId = next.GetProperty("id").GetGuid();
        var changed = await SendAsync(HttpMethod.Put, $"/api/v1/sales/price-lists/{nextId}/lines", token,
            new { lines = new[] { new { productId = gravel, unitPrice = 32.75m } } }, next.GetProperty("version").GetString());
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(changed)).GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal().Should().Be(32.75m);
        (await SendAsync(HttpMethod.Put, $"/api/v1/sales/price-lists/{nextId}/lines", token,
            new { lines = new[] { new { productId = gravel, unitPrice = 34m } } }, next.GetProperty("version").GetString()))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "the form was filled from an older version");

        var sameStart = await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", token, PriceList(site, null, 7, (gravel, 35m)));
        sameStart.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(sameStart)).GetProperty("code").GetString().Should().Be("sales.price_list_same_start");

        (await Json(await SendAsync(HttpMethod.Post, $"/api/v1/sales/price-lists/{nextId}/withdraw", token))).GetProperty("status").GetString().Should().Be("withdrawn");
        (await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", token, PriceList(site, null, 7, (gravel, 35m))))
            .StatusCode.Should().Be(HttpStatusCode.Created, "a withdrawn version frees its start day");

        var backDated = await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", token, PriceList(site, null, -1, (gravel, 29m)));
        backDated.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(backDated)).GetProperty("code").GetString().Should().Be("sales.price_list_back_dated");

        (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/sales/price-lists?siteId={site}&siteOnly=true", token))).Should().HaveCount(3);
    }

    [Fact]
    [Trait("TestCase", "TC-P3-SAL-003")]
    public async Task Unit_prices_are_masked_for_the_site_manager_and_read_only_for_the_weighmaster()
    {
        var (tenant, token) = await TenantAsync();
        var site = await SiteAsync(token, "EST");
        var gravel = await ProductAsync(token, "G-8/16", await MaterialAsync(token, "MAT-816", "product"));
        var list = (await Json(await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", token, PriceList(site, null, 0, (gravel, 30m))))).GetProperty("id").GetGuid();
        var manager = TestTokens.ForTenant(tenant, SalesHost.SiteManager);

        var seen = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sales/price-lists/{list}", manager));
        var line = seen.GetProperty("lines")[0];
        line.TryGetProperty("unitPrice", out _).Should().BeFalse("the value never leaves the server");
        line.GetProperty("maskedFields").EnumerateArray().Select(f => f.GetString()).Should().Contain("unitPrice");
        var quote = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sales/prices?siteId={site}&productId={gravel}", manager));
        quote.TryGetProperty("unitPrice", out _).Should().BeFalse();
        quote.GetProperty("source").GetString().Should().Be("site", "the rest of the answer is not sensitive");

        var weighmaster = TestTokens.ForTenant(tenant, SalesHost.Weighmaster);
        (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/sales/prices?siteId={site}&productId={gravel}", weighmaster)))
            .GetProperty("unitPrice").GetDecimal().Should().Be(30m, "the ticket shows the price");
        (await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", weighmaster, PriceList(site, null, 2, (gravel, 1m))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", TestTokens.ForTenant(tenant, SalesHost.Finance), PriceList(site, null, 2, (gravel, 31m))))
            .StatusCode.Should().Be(HttpStatusCode.Created, "finance manages prices");
    }

    [Fact]
    public async Task A_product_is_a_sold_fraction_and_an_archived_product_gets_no_new_price()
    {
        var (tenant, token) = await TenantAsync();
        var site = await SiteAsync(token, "OUEST");
        var rock = await MaterialAsync(token, "ROCK", "raw-rock");
        var fraction = await MaterialAsync(token, "MAT-816", "product");

        var refused = await SendAsync(HttpMethod.Post, "/api/v1/sales/products", token, new { code = "BRUT", name = "Tout-venant", materialId = rock, saleUnit = "t" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(refused)).GetProperty("code").GetString().Should().Be("sales.product_material_not_sold");

        var created = await Json(await SendAsync(HttpMethod.Post, "/api/v1/sales/products", token,
            new { code = "g-8/16", name = " Gravier 8/16 ", materialId = fraction, saleUnit = "m3-loose" }));
        created.GetProperty("code").GetString().Should().Be("G-8/16");
        created.GetProperty("name").GetString().Should().Be("Gravier 8/16");
        created.GetProperty("saleUnit").GetString().Should().Be("m3-loose");
        var id = created.GetProperty("id").GetGuid();
        (await SendAsync(HttpMethod.Post, "/api/v1/sales/products", token, new { code = "G-8/16", name = "Autre", materialId = fraction }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "the code is taken");

        var renamed = await SendAsync(HttpMethod.Put, $"/api/v1/sales/products/{id}", token,
            new { name = "Gravier concassé 8/16", materialId = fraction, saleUnit = "t" }, created.GetProperty("version").GetString());
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(renamed)).GetProperty("saleUnit").GetString().Should().Be("t");

        (await Json(await SendAsync(HttpMethod.Post, $"/api/v1/sales/products/{id}/archive", token))).GetProperty("status").GetString().Should().Be("archived");
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/sales/products", token))).Should().BeEmpty();
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/sales/products?includeArchived=true", token))).Should().ContainSingle();
        var unpriced = await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", token, PriceList(site, null, 0, (id, 30m)));
        unpriced.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(unpriced)).GetProperty("code").GetString().Should().Be("sales.price_list_product_unknown");

        (await Json(await SendAsync(HttpMethod.Post, $"/api/v1/sales/products/{id}/restore", token))).GetProperty("status").GetString().Should().Be("active");
        (await LedgerAsync(tenant)).Should().Be(
            "sales.product_created.v1,sales.product_changed.v1,sales.product_archived.v1,sales.product_restored.v1");
    }

    [Fact]
    public async Task Another_company_cannot_price_with_my_site_or_my_product()
    {
        var (_, mine) = await TenantAsync();
        var site = await SiteAsync(mine, "NORD");
        var gravel = await ProductAsync(mine, "G-8/16", await MaterialAsync(mine, "MAT-816", "product"));
        var (_, theirs) = await TenantAsync();
        var theirSite = await SiteAsync(theirs, "NORD");

        var foreignSite = await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", theirs, PriceList(site, null, 0, (gravel, 1m)));
        (await Json(foreignSite)).GetProperty("code").GetString().Should().Be("sales.price_list_site_unknown");
        var foreignProduct = await SendAsync(HttpMethod.Post, "/api/v1/sales/price-lists", theirs, PriceList(theirSite, null, 0, (gravel, 1m)));
        (await Json(foreignProduct)).GetProperty("code").GetString().Should().Be("sales.price_list_product_unknown");
        (await SendAsync(HttpMethod.Get, $"/api/v1/sales/products/{gravel}", theirs)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> MaterialAsync(string token, string code, string kind)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/materials", token, new
        {
            code,
            name = $"Matériau {code}",
            kind,
            densities = new { loose = 1.50m, compacted = 1.75m, moisture = 0.02m },
            effectiveFrom = Day(-30),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ProductAsync(string token, string code, Guid material)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/sales/products", token, new { code, name = $"Produit {code}", materialId = material, saleUnit = "t" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CustomerAsync(string token)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/sales/customers", token, new { code = "CLI-1", legalName = "Bâtisseurs du Cap" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
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
        var slug = $"p-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await Json(response)).GetProperty("id").GetGuid();
        return (tenant, TestTokens.ForTenant(tenant));
    }

    private async Task<string> LedgerAsync(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT string_agg(event_type, ',' ORDER BY seq) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type LIKE 'sales.%'", connection);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

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
}
