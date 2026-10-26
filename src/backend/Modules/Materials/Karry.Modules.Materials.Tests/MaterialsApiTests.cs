using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Materials.Tests;

/// <summary>
/// Materials over HTTP (US-P1-AST-04, US-P1-UNI-01): created with their first densities, each change in the company's
/// ledger, density versions only from tomorrow on, conversions with the version valid on a date.
/// </summary>
[Trait("Story", "US-P1-AST-04")]
public sealed class MaterialsApiTests(MaterialsHost host) : IClassFixture<MaterialsHost>
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The test tenants keep the default time zone (UTC).
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object Gravel(string code = "g-8/16") => new
    {
        code,
        name = "Gravier 8/16",
        kind = "product",
        densities = new { loose = 1.50m, compacted = 1.75m, moisture = 0.02m },
        effectiveFrom = Iso(Jan1),
    };

    [Fact]
    public async Task A_material_is_created_with_its_first_densities_and_ledgered()
    {
        var (tenant, token) = await TenantAsync();

        var created = await SendAsync(HttpMethod.Post, "/api/v1/materials", token, Gravel());

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var material = await Json(created);
        material.GetProperty("code").GetString().Should().Be("G-8/16");
        material.GetProperty("kind").GetString().Should().Be("product");
        var version = material.GetProperty("densities")[0];
        version.GetProperty("effectiveFrom").GetString().Should().Be("2026-01-01");
        version.GetProperty("bank").ValueKind.Should().Be(JsonValueKind.Null);
        version.GetProperty("loose").GetDecimal().Should().Be(1.5m);
        version.GetProperty("swell").ValueKind.Should().Be(JsonValueKind.Null, "no bank density");
        (await LedgerAsync(tenant)).Should().Be("materials.material_created.v1");

        (await SendAsync(HttpMethod.Post, "/api/v1/materials", token, Gravel("G-8/16"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/materials", token))).Should().ContainSingle();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-UNI-002")]
    public async Task A_conversion_uses_the_densities_valid_on_its_date()
    {
        var (_, token) = await TenantAsync();
        var id = await CreateAsync(token);
        var change = Today.AddDays(10);
        (await SendAsync(HttpMethod.Put, $"/api/v1/materials/{id}/densities/{Iso(change)}", token, new { loose = 1.60m, moisture = 0.02m }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // GE-01: 28.40 t at 1.50 t/m³ loose and 2 % moisture = 18.562 m³ loose (rounded for display).
        var before = await ConvertAsync(id, token, Iso(change.AddDays(-1)));
        decimal.Round(before.GetProperty("result").GetProperty("value").GetDecimal(), 3, MidpointRounding.AwayFromZero).Should().Be(18.562m);
        before.GetProperty("result").GetProperty("state").GetString().Should().Be("loose");
        before.GetProperty("densitiesFrom").GetString().Should().Be("2026-01-01");
        before.GetProperty("factors").EnumerateArray().Select(f => (f.GetProperty("name").GetString(), f.GetProperty("value").GetDecimal()))
            .Should().Equal(("density.loose", 1.5m), ("moisture", 0.02m));

        var after = await ConvertAsync(id, token, Iso(change));
        after.GetProperty("densitiesFrom").GetString().Should().Be(Iso(change));
        decimal.Round(after.GetProperty("result").GetProperty("value").GetDecimal(), 3, MidpointRounding.AwayFromZero).Should().Be(17.402m, "28.40 / (1.60 × 1.02)");

        var early = await SendAsync(HttpMethod.Post, $"/api/v1/materials/{id}/conversions", token, Conversion(Iso(Jan1.AddDays(-1))));
        early.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(early)).GetProperty("code").GetString().Should().Be("materials.no_density_version");
    }

    [Fact]
    public async Task Densities_change_only_from_tomorrow_and_every_change_is_ledgered()
    {
        var (tenant, token) = await TenantAsync();
        var id = await CreateAsync(token);

        foreach (var past in new[] { Today, Jan1 })
        {
            var refused = await SendAsync(HttpMethod.Put, $"/api/v1/materials/{id}/densities/{Iso(past)}", token, new { loose = 1.6m, moisture = 0.02m });
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(refused)).GetProperty("code").GetString().Should().Be("materials.date_in_past");
        }

        var tomorrow = Iso(Today.AddDays(1));
        (await SendAsync(HttpMethod.Put, $"/api/v1/materials/{id}/densities/{tomorrow}", token, new { bank = 2.56m, loose = 1.6m, moisture = 0.03m }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var material = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/materials/{id}", token));
        material.GetProperty("densities")[1].GetProperty("swell").GetDecimal().Should().Be(0.6m, "2.56 / 1.60 − 1");

        (await SendAsync(HttpMethod.Delete, $"/api/v1/materials/{id}/densities/{tomorrow}", token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Delete, $"/api/v1/materials/{id}/densities/{Iso(Jan1)}", token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Put, $"/api/v1/materials/{id}", token, new { name = "Gravillon 8/16", kind = "product" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, $"/api/v1/materials/{id}/archive", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Put, $"/api/v1/materials/{id}/densities/{tomorrow}", token, new { loose = 1.6m, moisture = 0.02m }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "archived");
        (await ConvertAsync(id, token, Iso(Today))).GetProperty("densitiesFrom").GetString().Should().Be("2026-01-01", "an archived material still converts");
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/materials", token))).Should().BeEmpty();
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/materials?includeArchived=true", token))).Should().ContainSingle();
        (await SendAsync(HttpMethod.Post, $"/api/v1/materials/{id}/restore", token)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await LedgerAsync(tenant)).Should().Be(
            "materials.material_created.v1,materials.density_version_set.v1,materials.density_version_removed.v1,"
            + "materials.material_changed.v1,materials.material_archived.v1,materials.material_restored.v1");
    }

    [Fact]
    public async Task An_operator_reads_and_converts_but_cannot_change_materials()
    {
        var (tenant, admin) = await TenantAsync();
        var id = await CreateAsync(admin);
        var operatorToken = TestTokens.ForTenant(tenant, MaterialsHost.Operator);

        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/materials", operatorToken))).Should().ContainSingle();
        (await SendAsync(HttpMethod.Post, $"/api/v1/materials/{id}/conversions", operatorToken, Conversion(null))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/v1/materials", operatorToken, Gravel("OTHER"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Put, $"/api/v1/materials/{id}/densities/{Iso(Today.AddDays(1))}", operatorToken, new { loose = 1.6m, moisture = 0m }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("""{"quantity":{"value":10,"unit":"m3","state":"bank"},"unit":"t"}""", "units.density_missing")]
    [InlineData("""{"quantity":{"value":10,"unit":"m3"},"unit":"t"}""", "units.quantity_invalid")]
    [InlineData("""{"quantity":{"value":10,"unit":"t","state":"loose"},"unit":"kg"}""", "units.quantity_invalid")]
    [InlineData("""{"quantity":{"value":10,"unit":"yd3","state":null},"unit":"t"}""", "units.quantity_invalid")]
    [InlineData("""{"quantity":{"value":10,"unit":"t"},"unit":"m3"}""", "units.quantity_invalid")]
    [InlineData("""{"quantity":{"value":100,"unit":"l"},"unit":"t"}""", "units.not_convertible")]
    [InlineData("""{"quantity":{"value":10,"unit":"t"},"unit":"m3","state":"loose","moisture":0.5}""", "materials.moisture_invalid")]
    public async Task Conversions_that_make_no_sense_are_explained(string body, string code)
    {
        var (_, token) = await TenantAsync();
        var id = await CreateAsync(token);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/materials/{id}/conversions") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be(code);
    }

    [Theory]
    [InlineData("""{"code":"G","name":"x","kind":"stone","densities":{"loose":1.5,"moisture":0}}""", "materials.kind_invalid")]
    [InlineData("""{"code":"G","name":"x","kind":"product"}""", "materials.density_required")]
    [InlineData("""{"code":"G","name":"x","kind":"product","densities":{"loose":0.9,"moisture":0}}""", "materials.density_invalid")]
    [InlineData("""{"code":"G","name":"x","kind":"raw-rock","densities":{"bank":2.0,"loose":1.95,"moisture":0}}""", "materials.swell_invalid")]
    [InlineData("""{"code":"G G","name":"x","kind":"product","densities":{"loose":1.5,"moisture":0}}""", "materials.code_invalid")]
    [InlineData("""{"code":"G","name":"","kind":"product","densities":{"loose":1.5,"moisture":0}}""", "materials.name_invalid")]
    public async Task Invalid_materials_are_explained(string body, string code)
    {
        var (_, token) = await TenantAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/materials") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        using var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be(code);
    }

    private static object Conversion(string? on) => new { quantity = new { value = 28.40m, unit = "t", state = (string?)null }, unit = "m3", state = "loose", on };

    private async Task<JsonElement> ConvertAsync(Guid id, string token, string on)
    {
        var response = await SendAsync(HttpMethod.Post, $"/api/v1/materials/{id}/conversions", token, Conversion(on));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await Json(response);
    }

    private async Task<Guid> CreateAsync(string token)
    {
        var created = await SendAsync(HttpMethod.Post, "/api/v1/materials", token, Gravel());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(created)).GetProperty("id").GetGuid();
    }

    private async Task<(Guid Tenant, string Token)> TenantAsync()
    {
        var slug = $"m-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await Json(response)).GetProperty("id").GetGuid();
        return (tenant, TestTokens.ForTenant(tenant));
    }

    private Task<string> LedgerAsync(Guid tenant) =>
        ScalarAsync<string>($"SELECT string_agg(event_type, ',' ORDER BY seq) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type LIKE 'materials.%'");

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
