using System.Net;
using System.Net.Http.Headers;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Testing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Identity.Tests.Realm;

/// <summary>
/// The API's real token validation (<see cref="TenancyExtensions.AddKarryTenancy"/>: issuer discovery, signature,
/// audience) against tokens Keycloak issues to the apps: the claims the realm maps are the claims the API reads.
/// </summary>
[Trait("Story", "US-P1-IAM-01")]
public sealed class ApiTokenValidationTests(KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private const string Password = "correct-horse-battery-staple";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_api_accepts_an_app_token_and_resolves_its_tenant()
    {
        var tenant = Guid.NewGuid();
        var username = $"a{Guid.NewGuid():N}"[..20];
        await keycloak.Server.CreateUserAsync(username, Password, new Dictionary<string, string> { ["tenant_id"] = tenant.ToString() }, cancellationToken: Cancel);
        using var browser = new KeycloakBrowser(keycloak.Server, "karry-portal", "http://localhost:5173");
        var final = await browser.LoginAsync(await browser.StartAsync(cancellationToken: Cancel), username, Password, Cancel);
        var token = (await browser.ExchangeAsync(final, Cancel)).Body.GetProperty("access_token").GetString()!;
        await using var app = await ApiAsync(keycloak.Server.Issuer);
        using var client = app.GetTestClient();

        using var anonymous = await client.GetAsync(new Uri("/whoami", UriKind.Relative), Cancel);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/whoami", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var authenticated = await client.SendAsync(request, Cancel);
        using var forged = new HttpRequestMessage(HttpMethod.Get, new Uri("/whoami", UriKind.Relative));
        forged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant));
        using var foreign = await client.SendAsync(forged, Cancel);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        authenticated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await authenticated.Content.ReadAsStringAsync(Cancel)).Should().Be(tenant.ToString());
        foreign.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a token not signed by the realm is rejected");
    }

    [Fact]
    public void An_http_authority_is_refused_unless_the_local_stack_opts_out()
    {
        static bool RequiresHttps(Dictionary<string, string?> settings)
        {
            var services = new ServiceCollection().AddKarryTenancy(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).RequireHttpsMetadata;
        }

        RequiresHttps([]).Should().BeTrue();
        RequiresHttps(new() { ["Authentication:RequireHttpsMetadata"] = "false" }).Should().BeFalse();
    }

    private static async Task<WebApplication> ApiAsync(string issuer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Authority"] = issuer,
            ["Authentication:Audience"] = "karry-api",
            ["Authentication:RequireHttpsMetadata"] = "false",
        });
        builder.Services.AddKarryTenancy(builder.Configuration);
        var app = builder.Build();
        app.UseKarryTenancy();
        app.MapGet("/whoami", (ITenantContext tenant) => tenant.Current?.ToString() ?? "none")
            .RequireAuthorization()
            .WithMetadata(new TenantRequired())
            .AllowAnyTenantMember();
        await app.StartAsync(Cancel);
        return app;
    }
}
