using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// Hardening plan H-R2: Keycloak stops. The API's real token settings (<see cref="TenancyExtensions.AddKarryTenancy"/>)
/// against a stand-in realm on a real socket that is stopped and started again at the same address: people already
/// signed in keep working on the cached keys, nothing answers 500, and the API trusts the realm again soon after it
/// returns, even when the API itself started during the outage.
/// </summary>
[Trait("Story", "P1.18a")]
public sealed class KeycloakOutageTests : IAsyncDisposable
{
    private static readonly RsaSecurityKey RealmKey = new(RSA.Create(2048)) { KeyId = "realm-key" };

    private readonly int port = FreePort();
    private WebApplication? realm;
    private WebApplication? api;

    private string Authority => $"http://127.0.0.1:{port}/realms/karry";

    public async ValueTask DisposeAsync()
    {
        foreach (var app in new[] { api, realm })
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task People_already_signed_in_keep_working_while_keycloak_is_down()
    {
        await StartRealmAsync();
        using var client = await StartApiAsync();
        (await CallAsync(client, Token())).Should().Be(HttpStatusCode.OK);

        await StopRealmAsync();

        for (var call = 0; call < 20; call++)
        {
            (await CallAsync(client, Token($"user-{call}"))).Should().Be(HttpStatusCode.OK, "the realm's keys are cached; only signing in needs Keycloak");
        }
    }

    [Fact]
    public async Task A_token_signed_by_an_unknown_key_during_the_outage_is_refused_quickly_never_a_500()
    {
        await StartRealmAsync();
        using var client = await StartApiAsync();
        (await CallAsync(client, Token())).Should().Be(HttpStatusCode.OK);
        await StopRealmAsync();

        var rotated = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "rotated-key" };
        var watch = Stopwatch.StartNew();
        var status = await CallAsync(client, Token(key: rotated));

        status.Should().Be(HttpStatusCode.Unauthorized);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "looking for the new key must not hang a request on a stopped realm");
    }

    [Fact]
    public async Task An_api_started_during_the_outage_trusts_keycloak_again_soon_after_it_returns()
    {
        using var client = await StartApiAsync();
        (await CallAsync(client, Token())).Should().Be(HttpStatusCode.Unauthorized, "no keys yet: fail closed, not 500");

        await StartRealmAsync();
        var watch = Stopwatch.StartNew();
        var status = HttpStatusCode.Unauthorized;
        while (status != HttpStatusCode.OK && watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            status = await CallAsync(client, Token());
        }

        status.Should().Be(HttpStatusCode.OK);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "every signed-in person is refused until the API fetches the keys again");
    }

    private string Token(string subject = "user-1", RsaSecurityKey? key = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = "karry-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", subject)]),
            SigningCredentials = new SigningCredentials(key ?? RealmKey, SecurityAlgorithms.RsaSha256),
        });

    private static async Task<HttpStatusCode> CallAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/whoami", UriKind.Relative));
        request.Headers.Authorization = new("Bearer", token);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private async Task<HttpClient> StartApiAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Authority"] = Authority,
            ["Authentication:Audience"] = "karry-api",
            ["Authentication:RequireHttpsMetadata"] = "false",
        });
        builder.Services.AddKarryApiConventions().AddKarryTenancy(builder.Configuration);
        api = builder.Build();
        api.UseKarryApiConventions();
        api.UseKarryTenancy();
        api.MapGet("/whoami", (HttpContext context) => context.User.FindFirst("sub")?.Value).RequireAuthorization();
        await api.StartAsync(TestContext.Current.CancellationToken);
        return api.GetTestClient();
    }

    /// <summary>The two realm addresses the API reads: the discovery document and the signing keys.</summary>
    private async Task StartRealmAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
        realm = builder.Build();
        realm.MapGet("/realms/karry/.well-known/openid-configuration", () => new Dictionary<string, string>
        {
            ["issuer"] = Authority,
            ["jwks_uri"] = $"{Authority}/protocol/openid-connect/certs",
        });
        realm.MapGet("/realms/karry/protocol/openid-connect/certs", () =>
        {
            var key = RealmKey.Rsa.ExportParameters(includePrivateParameters: false);
            return new
            {
                keys = new[]
                {
                    new { kty = "RSA", use = "sig", alg = "RS256", kid = RealmKey.KeyId, n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) },
                },
            };
        });
        await realm.StartAsync(TestContext.Current.CancellationToken);
    }

    private async Task StopRealmAsync()
    {
        await realm!.StopAsync(TestContext.Current.CancellationToken);
        await realm.DisposeAsync();
        realm = null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
