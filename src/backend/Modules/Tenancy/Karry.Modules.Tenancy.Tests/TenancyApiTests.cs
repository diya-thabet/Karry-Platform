using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Api;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Karry.Modules.Tenancy.Tests;

/// <summary>
/// The Tenancy module on real PostgreSQL, migrated by the migration runner as <c>karry_owner</c> and used as
/// <c>karry_app</c>, behind the real pipeline (conventions, tenancy, idempotency) with test-issuer tokens.
/// </summary>
public sealed class TenancyHost : IAsyncLifetime
{
    public const string BaseDomain = "karry.test";

    private PostgresServer? server;
    private WebApplication? app;

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PostgresServer.StartAsync();
        Database = await server.NewKarryDatabaseAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Karry"] = Database.App,
            ["Tenancy:BaseDomain"] = BaseDomain,
        });
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryTenancy(builder.Configuration);
        builder.Services.TrustTestIssuer();
        builder.Services.AddIdempotency(Database.App);
        builder.Services.AddModules(builder.Configuration, [new TenancyModule()]);
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
        app.UseIdempotency();
        app.MapModules();

        await MigrationRunner.MigrateAsync(app.Services, Database.Owner, CancellationToken.None);
        await app.StartAsync();
        Client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }

        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }
}

[Trait("Story", "US-P1-TEN-01")]
public sealed class TenancyApiTests(TenancyHost host) : IClassFixture<TenancyHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-006")]
    public async Task Provisioning_twice_with_the_same_request_creates_one_tenant_in_under_30_seconds()
    {
        var slug = UniqueSlug();
        var watch = Stopwatch.StartNew();

        var first = await ProvisionAsync(slug, "Carrière de Sfax", key: Guid.NewGuid().ToString());
        var second = await ProvisionAsync(slug, "Carrière de Sfax", key: Guid.NewGuid().ToString());
        watch.Stop();

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "the same slug and name again is the same tenant, not an error");
        (await IdOf(second)).Should().Be(await IdOf(first));
        (await CountAsync($"SELECT count(*) FROM tenancy.tenants WHERE slug = '{slug}'")).Should().Be(1);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-006")]
    public async Task Concurrent_provisioning_of_one_slug_creates_exactly_one_tenant()
    {
        var slug = UniqueSlug();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ProvisionAsync(slug, "Carrière Concurrente", Guid.NewGuid().ToString())));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK);
        (await Task.WhenAll(responses.Select(IdOf))).Distinct().Should().ContainSingle();
        (await CountAsync($"SELECT count(*) FROM tenancy.tenants WHERE slug = '{slug}'")).Should().Be(1);
    }

    [Fact]
    public async Task A_retry_with_the_same_idempotency_key_replays_the_first_response()
    {
        var slug = UniqueSlug();
        var key = Guid.NewGuid().ToString();

        var first = await ProvisionAsync(slug, "Carrière Rejouée", key);
        var retry = await ProvisionAsync(slug, "Carrière Rejouée", key);

        retry.StatusCode.Should().Be(HttpStatusCode.Created, "the stored first response is replayed");
        retry.Headers.GetValues(IdempotencyOptions.ReplayedHeaderName).Should().Equal("true");
        (await IdOf(retry)).Should().Be(await IdOf(first));
    }

    [Fact]
    public async Task A_slug_of_another_company_is_a_conflict()
    {
        var slug = UniqueSlug();
        await ProvisionAsync(slug, "Première Carrière", Guid.NewGuid().ToString());

        var response = await ProvisionAsync(slug, "Autre Carrière", Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(response)).Should().Be("tenant.slug_taken");
    }

    [Theory]
    [InlineData("ab", "tenant.slug_invalid")]
    [InlineData("admin", "tenant.slug_reserved")]
    [InlineData("xn--80ak6aa92e", "tenant.slug_invalid")]
    public async Task An_invalid_or_reserved_slug_is_refused(string slug, string code)
    {
        var response = await ProvisionAsync(slug, "Une Carrière", Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be(code);
    }

    [Fact]
    public async Task Only_platform_operators_provision_tenants()
    {
        var asTenantUser = await ProvisionAsync(UniqueSlug(), "Une Carrière", Guid.NewGuid().ToString(), TestTokens.ForTenant(Guid.CreateVersion7()));
        var anonymous = await ProvisionAsync(UniqueSlug(), "Une Carrière", Guid.NewGuid().ToString(), token: string.Empty);

        asTenantUser.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Provisioning_records_the_tenant_provisioned_event_in_the_same_transaction()
    {
        var slug = UniqueSlug();
        var id = await IdOf(await ProvisionAsync(slug, "Carrière Événement", Guid.NewGuid().ToString()));

        (await CountAsync($"SELECT count(*) FROM tenancy.outbox_messages WHERE type = 'tenancy.tenant_provisioned.v1' AND payload->>'tenantId' = '{id}'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task The_public_branding_of_a_company_is_readable_before_sign_in()
    {
        var slug = UniqueSlug();
        await ProvisionAsync(slug, "Carrière Publique", Guid.NewGuid().ToString());

        var known = await host.Client.GetAsync(new Uri($"/api/v1/tenancy/public/{slug}", UriKind.Relative), Ct);
        var unknown = await host.Client.GetAsync(new Uri($"/api/v1/tenancy/public/{UniqueSlug()}", UriKind.Relative), Ct);

        known.StatusCode.Should().Be(HttpStatusCode.OK);
        (await known.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("name").GetString().Should().Be("Carrière Publique");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-009")]
    public async Task A_token_works_on_its_own_company_address_and_is_refused_on_another()
    {
        var slugA = UniqueSlug();
        var slugB = UniqueSlug();
        var tenantA = await IdOf(await ProvisionAsync(slugA, "Carrière Adresse A", Guid.NewGuid().ToString()));
        await ProvisionAsync(slugB, "Carrière Adresse B", Guid.NewGuid().ToString());
        var tokenA = TestTokens.ForTenant(tenantA);

        var onOwnAddress = await CurrentAsync($"{slugA}.{TenancyHost.BaseDomain}", tokenA);
        var onOtherAddress = await CurrentAsync($"{slugB}.{TenancyHost.BaseDomain}", tokenA);
        var onUnknownAddress = await CurrentAsync($"{UniqueSlug()}.{TenancyHost.BaseDomain}", tokenA);
        var onPlatformAddress = await CurrentAsync($"api.{TenancyHost.BaseDomain}", tokenA);

        onOwnAddress.StatusCode.Should().Be(HttpStatusCode.OK);
        (await onOwnAddress.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("slug").GetString().Should().Be(slugA);
        onOtherAddress.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(onOtherAddress)).Should().Be("tenant.host_mismatch");
        onUnknownAddress.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        onPlatformAddress.StatusCode.Should().Be(HttpStatusCode.OK, "api.<domain> is a platform address, not a tenant's");
    }

    [Fact]
    public async Task The_current_company_is_the_tokens_tenant()
    {
        var slug = UniqueSlug();
        var id = await IdOf(await ProvisionAsync(slug, "Carrière Courante", Guid.NewGuid().ToString()));

        var response = await CurrentAsync(address: null, TestTokens.ForTenant(id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid().Should().Be(id);
    }

    private static string UniqueSlug() => $"t-{Guid.NewGuid():N}"[..20];

    private async Task<HttpResponseMessage> ProvisionAsync(string slug, string name, string key, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants")
        {
            Content = JsonContent.Create(new { slug, name }),
        };
        request.Headers.Add(IdempotencyOptions.HeaderName, key);
        token ??= TestTokens.ForPlatformOperator();
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> CurrentAsync(string? address, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenancy/current");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (address is not null)
        {
            request.Headers.Host = address;
        }

        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<Guid> IdOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
