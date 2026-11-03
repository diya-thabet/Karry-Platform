using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// Hardening plan H-I1: the API's own token rules (<see cref="TenancyExtensions.AddKarryTenancy"/>), with only the
/// trusted key swapped for a test one. Every way a token can be stale, foreign, unsigned, re-signed or tampered with is
/// a 401. Real Keycloak tokens are checked the same way in <c>ApiTokenValidationTests</c> (Identity module).
/// </summary>
[Trait("Story", "P1.18a")]
public sealed class TokenValidationTests : IAsyncLifetime
{
    private const string Issuer = "https://auth.karry.test/realms/karry";
    private const string Audience = "karry-api";

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly RsaSecurityKey Key = new(Rsa) { KeyId = "realm-key" };

    private WebApplication? app;
    private HttpClient client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Audience"] = Audience });
        builder.Services.AddKarryApiConventions().AddKarryTenancy(builder.Configuration);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = null;
            options.TokenValidationParameters.ValidIssuer = Issuer;
            options.TokenValidationParameters.IssuerSigningKey = Key;
        });
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
        app.MapGet("/whoami", (HttpContext context) => context.User.FindFirst("sub")?.Value).RequireAuthorization();
        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_valid_token_is_accepted()
    {
        (await CallAsync(Token())).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_is_refused_once_past_its_expiry_and_the_30_second_tolerance()
    {
        TenancyExtensions.TokenClockSkew.Should().Be(TimeSpan.FromSeconds(30));

        (await CallAsync(Token(d => (d.IssuedAt, d.NotBefore, d.Expires) = (Now(-400), Now(-400), Now(-10))))).Should().Be(HttpStatusCode.OK, "within the tolerance");
        (await CallAsync(Token(d => (d.IssuedAt, d.NotBefore, d.Expires) = (Now(-400), Now(-400), Now(-60))))).Should().Be(HttpStatusCode.Unauthorized,
            "a minute late is refused (the framework default, 5 minutes, would still accept it)");
    }

    [Fact]
    public async Task A_token_not_valid_yet_is_refused()
    {
        (await CallAsync(Token(d => (d.IssuedAt, d.NotBefore, d.Expires) = (Now(120), Now(120), Now(420))))).Should().Be(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string> Foreign => ["issuer", "audience", "key"];

    [Theory]
    [MemberData(nameof(Foreign))]
    public async Task A_token_of_another_issuer_audience_or_key_is_refused(string what)
    {
        using var other = RSA.Create(2048);
        var token = what switch
        {
            "issuer" => Token(d => d.Issuer = "https://auth.karry.test/realms/master"),
            "audience" => Token(d => d.Audience = "realm-management"),
            _ => Token(d => d.SigningCredentials = new SigningCredentials(new RsaSecurityKey(other) { KeyId = Key.KeyId }, SecurityAlgorithms.RsaSha256)),
        };

        (await CallAsync(token)).Should().Be(HttpStatusCode.Unauthorized, what);
    }

    [Fact]
    public async Task A_token_without_expiry_is_refused()
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim("sub", "user-1")]),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });

        (await CallAsync(token)).Should().Be(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string> Algorithms => ["none", "HS256", "RS512", "PS256"];

    /// <summary>Only RS256: an unsigned token, one signed with a secret, or with another algorithm of the same key.</summary>
    [Theory]
    [MemberData(nameof(Algorithms))]
    public async Task Only_the_realms_signature_algorithm_is_accepted(string algorithm)
    {
        var token = algorithm switch
        {
            "none" => Unsigned(Token()),
            // The classic confusion attack: HMAC with the public key's bytes as the secret.
            "HS256" => Token(d => d.SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Rsa.ExportSubjectPublicKeyInfo()) { KeyId = Key.KeyId }, SecurityAlgorithms.HmacSha256)),
            "RS512" => Token(d => d.SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha512)),
            _ => Token(d => d.SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSsaPssSha256)),
        };

        (await CallAsync(token)).Should().Be(HttpStatusCode.Unauthorized, algorithm);
    }

    [Fact]
    public async Task A_token_whose_claims_were_changed_after_signing_is_refused()
    {
        var parts = Token().Split('.');
        var claims = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("\"user-1\"", "\"admin-1\"", StringComparison.Ordinal);

        (await CallAsync($"{parts[0]}.{Base64UrlEncoder.Encode(claims)}.{parts[2]}")).Should().Be(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string> Garbage => ["", "x", "a.b.c", "eyJhbGciOiJSUzI1NiJ9..", new string('a', 20_000)];

    [Theory]
    [MemberData(nameof(Garbage))]
    public async Task Garbage_in_the_header_is_a_401_never_a_500(string token)
    {
        (await CallAsync(token)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_is_only_read_from_the_authorization_header()
    {
        using var response = await client.GetAsync(new Uri($"/whoami?access_token={Token()}", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a token in a URL ends up in logs and browser history");
    }

    private static DateTime Now(int seconds) => DateTime.UtcNow.AddSeconds(seconds);

    private static string Token(Action<SecurityTokenDescriptor>? change = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = Now(0),
            NotBefore = Now(0),
            Expires = Now(300),
            Subject = new ClaimsIdentity([new Claim("sub", "user-1")]),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        };
        change?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>The same claims with <c>"alg": "none"</c> and no signature.</summary>
    private static string Unsigned(string token) =>
        $"{Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{token.Split('.')[1]}.";

    private async Task<HttpStatusCode> CallAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/whoami", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
