using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Organization.Tests;

/// <summary>
/// Sites over HTTP (US-P1-ORG-01): created, changed, archived and restored by an administrator, in the company's
/// ledger, stored as PostGIS geometry; a site role sees only its sites; the site limit follows the soft-limit rules.
/// </summary>
[Trait("Story", "US-P1-ORG-01")]
public sealed class SitesApiTests(OrganizationHost host) : IClassFixture<OrganizationHost>
{
    private static readonly double[][] Square =
    [
        [10.0000, 36.8000], [10.0112, 36.8000], [10.0112, 36.8090], [10.0000, 36.8090], [10.0000, 36.8000],
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Input(string code, string name = "Carrière Nord", double[][]? ring = null) => new
    {
        code,
        name,
        type = "quarry",
        address = new { line = "Route de Bizerte km 12", city = "Tunis", region = (string?)null, postalCode = "1000", country = "TN" },
        location = new { latitude = 36.8045, longitude = 10.0056 },
        boundary = ring is null ? null : new[] { ring }, // GeoJSON Polygon coordinates: a list of rings
        timeZone = "Africa/Tunis",
        currency = (string?)null,
    };

    [Fact]
    public async Task An_administrator_creates_a_site_with_its_pin_and_boundary_stored_as_geometry_and_ledgered()
    {
        var tenant = await ProvisionAsync();

        var created = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", TestTokens.ForTenant(tenant, "admin-1"), Input("car-nord", ring: Square));

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var site = await Json(created);
        site.GetProperty("code").GetString().Should().Be("CAR-NORD");
        site.GetProperty("status").GetString().Should().Be("active");
        site.GetProperty("location").GetProperty("latitude").GetDouble().Should().Be(36.8045);
        site.GetProperty("boundary").GetProperty("type").GetString().Should().Be("Polygon");
        site.GetProperty("boundary").GetProperty("coordinates")[0].GetArrayLength().Should().Be(5);
        var id = site.GetProperty("id").GetGuid();

        (await ScalarAsync<string>($"SELECT ST_AsText(location) FROM organization.sites WHERE id = '{id}'")).Should().Be("POINT(10.0056 36.8045)");
        (await ScalarAsync<bool>($"SELECT ST_Contains(boundary, location) FROM organization.sites WHERE id = '{id}'")).Should().BeTrue("PostGIS answers spatial questions");
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'organization.site_created.v1'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_site_is_changed_archived_and_restored_each_once_in_the_ledger_and_its_code_stays_taken()
    {
        var tenant = await ProvisionAsync();
        var token = TestTokens.ForTenant(tenant);
        var id = (await Json(await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, Input("CAR-SUD")))).GetProperty("id").GetGuid();

        (await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{id}", token, Input("CAR-SUD", "Carrière du Sud"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{id}", token, Input("CAR-SUD", "Carrière du Sud"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var archived = await SendAsync(HttpMethod.Post, $"/api/v1/organization/sites/{id}/archive", token);
        (await Json(archived)).GetProperty("status").GetString().Should().Be("archived");

        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/organization/sites", token))).Should().BeEmpty("archived sites leave the default list");
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/organization/sites?includeArchived=true", token))).Should().ContainSingle();
        (await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, Input("car-sud"))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "an archived site keeps its code");

        (await Json(await SendAsync(HttpMethod.Post, $"/api/v1/organization/sites/{id}/restore", token))).GetProperty("status").GetString().Should().Be("active");
        (await ScalarAsync<string>($"SELECT string_agg(event_type, ',' ORDER BY seq) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type LIKE 'organization.%'"))
            .Should().Be("organization.site_created.v1,organization.site_changed.v1,organization.site_archived.v1,organization.site_restored.v1");
    }

    [Fact]
    public async Task A_site_role_sees_only_its_sites_and_cannot_create_any()
    {
        var tenant = await ProvisionAsync();
        var admin = TestTokens.ForTenant(tenant);
        var north = (await Json(await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", admin, Input("NORD")))).GetProperty("id").GetGuid();
        var south = (await Json(await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", admin, Input("SUD")))).GetProperty("id").GetGuid();
        host.SiteOf[OrganizationHost.SiteManager] = north;
        var manager = TestTokens.ForTenant(tenant, OrganizationHost.SiteManager);

        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/organization/sites", manager))).Select(s => s.GetProperty("code").GetString()).Should().Equal("NORD");
        (await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{north}", manager)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{south}", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound, "another site does not exist for them");
        (await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", manager, Input("EST"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/organization/sites/{north}/archive", manager)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/organization/sites", TestTokens.ForTenant(tenant, OrganizationHost.Executive))))
            .Should().HaveCount(2, "a tenant-wide role sees every site");
    }

    [Fact]
    public async Task The_site_count_follows_the_soft_limit_and_archiving_frees_a_place()
    {
        var tenant = await ProvisionAsync();
        var token = TestTokens.ForTenant(tenant);
        host.Limits.ByTenant[tenant] = 1; // one site bought: the ceiling is 2 (10 % grace, at least one)

        (await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, Input("S1"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, Input("S2"));
        second.StatusCode.Should().Be(HttpStatusCode.Created, "over the limit within the grace: allowed");
        var third = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, Input("S3"));
        third.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var refusal = await Json(third);
        refusal.GetProperty("code").GetString().Should().Be("billing.limit_reached");
        refusal.GetProperty("metric").GetString().Should().Be("sites");

        var id = (await Json(second)).GetProperty("id").GetGuid();
        await SendAsync(HttpMethod.Post, $"/api/v1/organization/sites/{id}/archive", token);
        (await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, Input("S3"))).StatusCode.Should().Be(HttpStatusCode.Created, "an archived site frees its place");
        (await SendAsync(HttpMethod.Post, $"/api/v1/organization/sites/{id}/restore", token)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "a restore counts as a creation");
    }

    [Theory]
    [InlineData("organization.site_code_invalid")]
    [InlineData("organization.boundary_self_intersecting")]
    public async Task Invalid_sites_are_refused_and_nothing_is_stored(string code)
    {
        var tenant = await ProvisionAsync();
        var input = code == "organization.site_code_invalid"
            ? Input("x")
            : Input("BOW", ring: [[10.0, 36.8], [10.01, 36.81], [10.0, 36.81], [10.01, 36.8], [10.0, 36.8]]);

        var response = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", TestTokens.ForTenant(tenant), input);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be(code);
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.sites WHERE tenant_id = '{tenant}'")).Should().Be(0);
    }

    [Fact]
    public async Task The_database_refuses_an_invalid_boundary_even_without_the_api()
    {
        var tenant = await ProvisionAsync();
        var id = (await Json(await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", TestTokens.ForTenant(tenant), Input("DB")))).GetProperty("id").GetGuid();

        var write = async () => await ScalarAsync<int>(
            $"UPDATE organization.sites SET boundary = ST_GeomFromText('POLYGON((10 36.8, 10.01 36.81, 10 36.81, 10.01 36.8, 10 36.8))', 4326) WHERE id = '{id}' RETURNING 1");

        (await write.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_sites_boundary_valid");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task A_change_made_from_an_old_version_is_refused_and_the_newer_change_is_kept()
    {
        // Hardening plan H-C1: two people opened the same site; the second to save must not overwrite the first.
        var tenant = await ProvisionAsync();
        var token = TestTokens.ForTenant(tenant);
        var created = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, Input("CAR-SUD"));
        var id = (await Json(created)).GetProperty("id").GetGuid();
        var opened = await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{id}", token);
        var version = opened.Headers.ETag!.Tag;
        (await Json(opened)).GetProperty("version").GetString().Should().Be(version, "body and header carry the same version");

        var first = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{id}", token, Input("CAR-SUD", "Carrière de Amina"), ifMatch: version);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag!.Tag.Should().NotBe(version, "every saved change makes a new version");

        var second = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{id}", token, Input("CAR-SUD", "Carrière de Karim"), ifMatch: version);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(second)).GetProperty("code").GetString().Should().Be("request.version_conflict");

        (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{id}", token))).GetProperty("name").GetString().Should().Be("Carrière de Amina");
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'organization.site_changed.v1'"))
            .Should().Be(1, "the refused change left no trace");

        var retried = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{id}", token, Input("CAR-SUD", "Carrière de Karim"), ifMatch: first.Headers.ETag.Tag);
        retried.StatusCode.Should().Be(HttpStatusCode.OK, "from the current version, the same change goes through");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task Of_many_saves_from_one_version_at_the_same_moment_exactly_one_is_applied()
    {
        // The race between loading and saving: the check in the UPDATE (xmin), not only the comparison in the service.
        var tenant = await ProvisionAsync();
        var token = TestTokens.ForTenant(tenant);
        var created = await Json(await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, Input("CAR-SUD")));
        var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("version").GetString()!;

        var saves = await Task.WhenAll(Enumerable.Range(1, 8).Select(n =>
            SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{id}", token, Input("CAR-SUD", $"Nom {n}"), ifMatch: version)));

        saves.Count(s => s.StatusCode == HttpStatusCode.OK).Should().Be(1);
        saves.Count(s => s.StatusCode == HttpStatusCode.Conflict).Should().Be(7, "every other save is a version conflict, never a 500");
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'organization.site_changed.v1'"))
            .Should().Be(1);
    }

    [Theory]
    [Trait("Story", "P1.18a")]
    [InlineData("4711")]
    [InlineData("\"abc\"")]
    [InlineData("\"1\", \"2\"")]
    [InlineData("\"-1\"")]
    public async Task A_malformed_if_match_is_refused_and_nothing_changes(string ifMatch)
    {
        var tenant = await ProvisionAsync();
        var token = TestTokens.ForTenant(tenant);
        var id = (await Json(await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", token, Input("CAR-SUD")))).GetProperty("id").GetGuid();

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/organization/sites/{id}", token, Input("CAR-SUD", "Autre"), ifMatch: ifMatch);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be("request.if_match_invalid");
        (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/organization/sites/{id}", token))).GetProperty("name").GetString().Should().Be("Carrière Nord");
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"o-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
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

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

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
