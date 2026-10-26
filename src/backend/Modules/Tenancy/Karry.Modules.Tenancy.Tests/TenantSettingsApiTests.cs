using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Tenancy.Tests;

/// <summary>
/// Company settings over HTTP (US-P1-TEN-04, TC-P1-TEN-007): a change is in the company's ledger and every member sees
/// it at their next read with the token they already have; only holders of <c>tenancy.settings.manage</c> change it.
/// </summary>
[Trait("Story", "US-P1-TEN-04")]
public sealed class TenantSettingsApiTests(TenancyHost host) : IClassFixture<TenancyHost>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Settings(string currency = "TND", string colour = "#1F4E79") => new
    {
        legalName = "Carrières du Nord SA",
        primaryColour = colour,
        language = "en",
        currency,
        unitSystem = "metric",
        timeZone = "Africa/Tunis",
        fiscalYearStartMonth = 7,
    };

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-007")]
    public async Task A_change_is_ledgered_and_seen_with_the_same_token_without_signing_in_again()
    {
        var (tenant, slug) = await ProvisionAsync();
        var member = TestTokens.ForTenant(tenant, TenancyHost.Executive);
        var before = await Json(await SendAsync(HttpMethod.Get, "/api/v1/tenancy/current", member));
        before.GetProperty("currency").GetString().Should().Be("EUR");
        before.GetProperty("legalName").GetString().Should().StartWith("Carrière");

        var changed = await SendAsync(HttpMethod.Put, "/api/v1/tenancy/settings", TestTokens.ForTenant(tenant, "admin-1"), Settings());
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Put, "/api/v1/tenancy/settings", TestTokens.ForTenant(tenant, "admin-1"), Settings()))
            .StatusCode.Should().Be(HttpStatusCode.OK, "repeating changes nothing");

        var after = await Json(await SendAsync(HttpMethod.Get, "/api/v1/tenancy/current", member));
        after.GetProperty("legalName").GetString().Should().Be("Carrières du Nord SA");
        after.GetProperty("currency").GetString().Should().Be("TND");
        after.GetProperty("language").GetString().Should().Be("en");
        after.GetProperty("unitSystem").GetString().Should().Be("metric");
        after.GetProperty("timeZone").GetString().Should().Be("Africa/Tunis");
        after.GetProperty("fiscalYearStartMonth").GetInt32().Should().Be(7);
        after.GetProperty("primaryColour").GetString().Should().Be("#1F4E79");

        (await LedgerAsync(tenant, "tenancy.tenant_settings_changed.v1")).Should().Be(1, "one change, one entry; the repeat is not a change");
        var payload = await ScalarAsync<string>(
            $"SELECT payload::text FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'tenancy.tenant_settings_changed.v1'");
        payload.Should().Contain("\"EUR\"").And.Contain("\"TND\"").And.Contain("admin-1", "before, after and who");

        var branding = await Json(await host.Client.GetAsync(new Uri($"/api/v1/tenancy/public/{slug}", UriKind.Relative), Ct));
        branding.GetProperty("primaryColour").GetString().Should().Be("#1F4E79", "the sign-in page shows the colour");
    }

    [Fact]
    public async Task Only_holders_of_the_settings_permission_change_them()
    {
        var (tenant, _) = await ProvisionAsync();

        var refused = await SendAsync(HttpMethod.Put, "/api/v1/tenancy/settings", TestTokens.ForTenant(tenant, TenancyHost.Executive), Settings());
        var logo = await SendLogoAsync(TestTokens.ForTenant(tenant, TenancyHost.Executive), Png);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        logo.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LedgerAsync(tenant, "tenancy.tenant_settings_changed.v1")).Should().Be(0);
        (await SendAsync(HttpMethod.Put, "/api/v1/tenancy/settings", TestTokens.ForPlatformOperator(), Settings()))
            .StatusCode.Should().NotBe(HttpStatusCode.OK, "settings belong to a company");
    }

    [Theory]
    [InlineData("TND", "#FFD700", "tenant.colour_contrast")]
    [InlineData("EUO", "#1F4E79", "tenant.currency_unsupported")]
    public async Task Invalid_settings_are_refused_and_nothing_changes(string currency, string colour, string code)
    {
        var (tenant, _) = await ProvisionAsync();

        var response = await SendAsync(HttpMethod.Put, "/api/v1/tenancy/settings", TestTokens.ForTenant(tenant), Settings(currency, colour));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be(code);
        (await Json(await SendAsync(HttpMethod.Get, "/api/v1/tenancy/current", TestTokens.ForTenant(tenant)))).GetProperty("currency").GetString().Should().Be("EUR");
    }

    [Fact]
    public async Task A_logo_is_stored_served_publicly_as_an_image_and_ledgered_by_its_fingerprint()
    {
        var (tenant, slug) = await ProvisionAsync();

        var set = await SendLogoAsync(TestTokens.ForTenant(tenant), Png);
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        var address = (await Json(set)).GetProperty("logoUrl").GetString()!;
        address.Should().StartWith($"/api/v1/tenancy/public/{slug}/logo?v=");

        var served = await host.Client.GetAsync(new Uri(address, UriKind.Relative), Ct);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.Should().Be("image/png", "the type comes from the content");
        served.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        (await served.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(Png);
        (await Json(await host.Client.GetAsync(new Uri($"/api/v1/tenancy/public/{slug}", UriKind.Relative), Ct)))
            .GetProperty("logoUrl").GetString().Should().Be(address);

        (await LedgerAsync(tenant, "tenancy.tenant_logo_changed.v1")).Should().Be(1);
        (await ScalarAsync<string>($"SELECT payload::text FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'tenancy.tenant_logo_changed.v1'"))
            .Should().NotContain(Convert.ToBase64String(Png), "the ledger keeps the fingerprint, not the image");

        (await SendAsync(HttpMethod.Delete, "/api/v1/tenancy/settings/logo", TestTokens.ForTenant(tenant))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client.GetAsync(new Uri(address, UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LedgerAsync(tenant, "tenancy.tenant_logo_changed.v1")).Should().Be(2);
    }

    [Fact]
    public async Task Svg_and_oversized_logos_are_refused()
    {
        var (tenant, _) = await ProvisionAsync();
        var big = new byte[(256 * 1024) + 10];
        Png.CopyTo(big, 0);

        var svg = await SendLogoAsync(TestTokens.ForTenant(tenant), "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"u8.ToArray(), "image/png");
        var oversized = await SendLogoAsync(TestTokens.ForTenant(tenant), big);

        (await Json(svg)).GetProperty("code").GetString().Should().Be("tenant.logo_type_unsupported", "the declared type is not trusted");
        (await Json(oversized)).GetProperty("code").GetString().Should().Be("tenant.logo_too_large");
        (await LedgerAsync(tenant, "tenancy.tenant_logo_changed.v1")).Should().Be(0);
    }

    private async Task<(Guid Id, string Slug)> ProvisionAsync()
    {
        var slug = $"s-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return ((await Json(response)).GetProperty("id").GetGuid(), slug);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
            request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> SendLogoAsync(string token, byte[] content, string type = "image/png")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/tenancy/settings/logo") { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private Task<long> LedgerAsync(Guid tenant, string type) =>
        ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = '{type}'");

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
