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
    [Trait("Story", "P1.18a")]
    public async Task Real_realm_tokens_cannot_be_used_for_another_audience_altered_or_stripped_of_their_signature()
    {
        // Hardening plan H-I1 on tokens Keycloak really issued (TokenValidationTests covers the full matrix offline).
        var tenant = Guid.NewGuid();
        var username = $"a{Guid.NewGuid():N}"[..20];
        await keycloak.Server.CreateUserAsync(username, Password, new Dictionary<string, string> { ["tenant_id"] = tenant.ToString() }, cancellationToken: Cancel);
        using var browser = new KeycloakBrowser(keycloak.Server, "karry-portal", "http://localhost:5173");
        var final = await browser.LoginAsync(await browser.StartAsync(cancellationToken: Cancel), username, Password, Cancel);
        var token = (await browser.ExchangeAsync(final, Cancel)).Body.GetProperty("access_token").GetString()!;
        var parts = token.Split('.');
        var otherTenant = System.Text.Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(parts[1]))
            .Replace(tenant.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal);
        await using var app = await ApiAsync(keycloak.Server.Issuer);
        using var client = app.GetTestClient();

        async Task<HttpStatusCode> CallAsync(string bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/whoami", UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await client.SendAsync(request, Cancel);
            return response.StatusCode;
        }

        (await CallAsync(token)).Should().Be(HttpStatusCode.OK, "the control: the untouched token works");
        (await CallAsync(await keycloak.Server.ServiceTokenAsync(Cancel))).Should().Be(HttpStatusCode.Unauthorized,
            "the realm's most powerful token (Identity's admin API account) is not for the API: wrong audience");
        (await CallAsync($"{parts[0]}.{Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(otherTenant)}.{parts[2]}")).Should().Be(HttpStatusCode.Unauthorized,
            "moving the token to another tenant breaks its signature");
        (await CallAsync($"{Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{parts[1]}.")).Should().Be(HttpStatusCode.Unauthorized,
            "the same claims without a signature");
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
