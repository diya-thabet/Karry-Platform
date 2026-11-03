using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// Request limits (threat T-12, hardening plan H-A1–H-A3): a tablet, a person, a company or an IP address past its
/// limit gets <c>429 request.rate_limited</c> with <c>Retry-After</c>, and nobody else is slowed down.
/// </summary>
[Trait("Story", "P1.18a")]
public sealed class RateLimitTests
{
    private const string Issuer = "https://auth.karry.test/realms/karry";
    private const string Audience = "karry-api";
    private const string AddressHeader = "X-Test-Address";
    private static readonly Guid TenantA = Guid.Parse("01970000-0000-7000-8000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("01970000-0000-7000-8000-00000000000b");
    private static readonly RsaSecurityKey SharedKey = new(RSA.Create(2048)) { KeyId = "issuer" };

    [Fact]
    public async Task A_person_past_the_limit_is_refused_with_retry_after_and_a_colleague_is_not()
    {
        await using var host = await Host.StartAsync(("PerPersonPerMinute", "3"));
        var alice = Person("alice", TenantA);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/me", bearer: alice)).StatusCode);
        }

        using var refused = await host.GetAsync("/me", bearer: alice);
        await AssertRateLimitedAsync(refused);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/me", bearer: Person("bob", TenantA))).StatusCode);
    }

    [Fact]
    public async Task One_company_past_its_limit_does_not_slow_down_another()
    {
        await using var host = await Host.StartAsync(("PerPersonPerMinute", "3"), ("PerTenantPerMinute", "5"));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/me", bearer: Person("alice", TenantA))).StatusCode);
        }

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/me", bearer: Person("bob", TenantA))).StatusCode);
        }

        // Bob is under his own limit, but company A has used its five.
        await AssertRateLimitedAsync(await host.GetAsync("/me", bearer: Person("bob", TenantA)));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/me", bearer: Person("carol", TenantB))).StatusCode);
    }

    [Fact]
    public async Task A_flooding_tablet_is_refused_and_the_other_tablets_keep_syncing()
    {
        await using var host = await Host.StartAsync(("PerDevicePerMinute", "3"));
        var flooding = host.Tablet(TenantA);
        var other = host.Tablet(TenantA);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/device", device: flooding.Sign("GET", "/device"))).StatusCode);
        }

        await AssertRateLimitedAsync(await host.GetAsync("/device", device: flooding.Sign("GET", "/device")));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/device", device: other.Sign("GET", "/device"))).StatusCode);
    }

    [Fact]
    public async Task Forged_assertions_naming_a_real_tablet_do_not_use_up_its_limit()
    {
        // The tablet's limit is counted only once its signature checked: a stranger who knows its ID cannot block it.
        await using var host = await Host.StartAsync(("PerDevicePerMinute", "2"));
        var tablet = host.Tablet(TenantA);
        var forger = new HostTablet(tablet.Id, TenantA, ECDsa.Create(ECCurve.NamedCurves.nistP256));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/device", device: forger.Sign("GET", "/device"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/device", device: tablet.Sign("GET", "/device"))).StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_are_limited_per_address()
    {
        await using var host = await Host.StartAsync(("AnonymousPerAddressPerMinute", "3"));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/public", address: "203.0.113.7")).StatusCode);
        }

        await AssertRateLimitedAsync(await host.GetAsync("/public", address: "203.0.113.7"));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/public", address: "198.51.100.20")).StatusCode);

        // A signed-in person behind the same address counts against their own limit, not the anonymous one.
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/public", address: "203.0.113.7", bearer: Person("alice", TenantA))).StatusCode);
    }

    [Fact]
    public async Task A_flood_of_bad_tokens_from_one_address_is_cut_before_authentication()
    {
        await using var host = await Host.StartAsync(("PerAddressPerMinute", "4"));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetAsync("/me", address: "203.0.113.9", bearer: "not-a-token")).StatusCode);
        }

        await AssertRateLimitedAsync(await host.GetAsync("/me", address: "203.0.113.9", bearer: "not-a-token"));
        await AssertRateLimitedAsync(await host.GetAsync("/me", address: "203.0.113.9", bearer: Person("alice", TenantA)));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/me", address: "198.51.100.20", bearer: Person("alice", TenantA))).StatusCode);
    }

    [Fact]
    public async Task Only_a_known_load_balancer_can_name_the_callers_address()
    {
        await using var host = await Host.StartAsync(("AnonymousPerAddressPerMinute", "2"), ("ReverseProxy:KnownNetworks:0", "10.0.0.0/8"));

        // Behind the load balancer (10.0.0.5), each caller counts against their own address.
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/public", address: "10.0.0.5", forwardedFor: "203.0.113.7")).StatusCode);
        }

        await AssertRateLimitedAsync(await host.GetAsync("/public", address: "10.0.0.5", forwardedFor: "203.0.113.7"));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/public", address: "10.0.0.5", forwardedFor: "198.51.100.20")).StatusCode);

        // A caller outside the known networks cannot escape its limit by inventing addresses.
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/public", address: "192.0.2.50", forwardedFor: "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/public", address: "192.0.2.50", forwardedFor: "198.51.100.2")).StatusCode);
        await AssertRateLimitedAsync(await host.GetAsync("/public", address: "192.0.2.50", forwardedFor: "198.51.100.3"));
    }

    [Fact]
    public async Task Health_probes_are_never_limited()
    {
        await using var host = await Host.StartAsync(("PerAddressPerMinute", "1"), ("AnonymousPerAddressPerMinute", "1"));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/health/live")).StatusCode);
        }
    }

    [Fact]
    public async Task Disabled_limits_let_everything_through()
    {
        await using var host = await Host.StartAsync(("Enabled", "false"), ("AnonymousPerAddressPerMinute", "1"));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/public")).StatusCode);
        }
    }

    [Fact]
    public async Task A_host_that_did_not_add_the_limits_is_not_limited()
    {
        await using var host = await Host.StartAsync(withLimits: false);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/public")).StatusCode);
        }
    }

    [Fact]
    public void Production_refuses_to_start_with_the_limits_off()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimits:Enabled"] = "false" }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddKarryRateLimits(configuration, new Environment(Environments.Production)));
        Assert.Contains("RateLimits:Enabled", error.Message, StringComparison.Ordinal);
        new ServiceCollection().AddKarryRateLimits(configuration, new Environment(Environments.Development));
    }

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var seconds = Assert.Single(response.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture), 1, 60);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("request.rate_limited", problem.GetProperty("code").GetString());
        Assert.Equal("https://karry.app/problems/request.rate_limited", problem.GetProperty("type").GetString());
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture), problem.GetProperty("retryAfterSeconds").GetInt32());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    private static string Person(string subject, Guid tenant) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim(TenantResolutionMiddleware.TenantClaim, tenant.ToString())]),
            SigningCredentials = new SigningCredentials(SharedKey, SecurityAlgorithms.RsaSha256),
        });

    private sealed class Host(WebApplication app, HttpClient client, Devices devices) : IAsyncDisposable
    {
        public static async Task<Host> StartAsync(params (string Key, string Value)[] limits) => await StartAsync(true, limits);

        public static async Task<Host> StartAsync(bool withLimits, params (string Key, string Value)[] limits)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" }); // the default, Production, refuses the limits off
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(limits.Select(l => new KeyValuePair<string, string?>(l.Key.Contains(':', StringComparison.Ordinal) ? l.Key : $"{RateLimitOptions.Section}:{l.Key}", l.Value)));
            builder.Services.AddKarryApiConventions();
            builder.Services.AddKarryTenancy(builder.Configuration);
            builder.Services.AddKarryReverseProxy(builder.Configuration);
            if (withLimits)
            {
                builder.Services.AddKarryRateLimits(builder.Configuration, builder.Environment);
            }

            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.TokenValidationParameters = new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = SharedKey };
            });
            var devices = new Devices();
            builder.Services.AddSingleton<IDeviceDirectory>(devices);
            var app = builder.Build();

            // The test server has no socket: the caller's address comes from a test header.
            app.Use((context, next) =>
            {
                if (context.Request.Headers.TryGetValue(AddressHeader, out var address))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                }

                return next(context);
            });
            app.UseKarryReverseProxy();
            app.UseKarryApiConventions();
            app.UseKarryTenancy();
            app.MapGet("/health/live", () => "up").AllowAnonymous();
            app.MapGet("/public", () => "branding").AllowAnonymous();
            app.MapGet("/me", (ClaimsPrincipal user) => user.FindFirst("sub")?.Value).RequireAuthorization();
            app.MapGet("/device", (ClaimsPrincipal user) => user.FindFirst(DeviceAuthenticationHandler.DeviceIdClaim)?.Value).RequireDevice();
            await app.StartAsync();
            return new Host(app, app.GetTestClient(), devices);
        }

        public HostTablet Tablet(Guid tenant)
        {
            var tablet = new HostTablet(Guid.CreateVersion7(), tenant, ECDsa.Create(ECCurve.NamedCurves.nistP256));
            devices.Add(tablet);
            return tablet;
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string address = "192.0.2.1", string? bearer = null, string? device = null, string? forwardedFor = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Add(AddressHeader, address);
            if (forwardedFor is not null)
            {
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            }

            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            else if (device is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(DeviceAuthenticationHandler.SchemeName, device);
            }

            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed record HostTablet(Guid Id, Guid Tenant, ECDsa Key)
    {
        public string Sign(string method, string path)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Part(new { alg = "ES256", typ = "karry-device+jwt", kid = Id.ToString("D") });
            var payload = Part(new { tid = Tenant.ToString("D"), iat = now, exp = now + 60, jti = Guid.NewGuid().ToString("N"), htm = method, htu = path });
            var signature = Key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"{header}.{payload}.{Base64Url.EncodeToString(signature)}";
        }

        private static string Part(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));
    }

    private sealed class Devices : IDeviceDirectory
    {
        private readonly Dictionary<Guid, DeviceKey> keys = [];

        public void Add(HostTablet tablet)
        {
            var point = tablet.Key.ExportParameters(false).Q;
            keys[tablet.Id] = new DeviceKey(tablet.Id, Guid.CreateVersion7(), point.X!, point.Y!, Revoked: false);
        }

        public Task<DeviceKey?> FindAsync(Guid deviceId, CancellationToken cancellationToken) => Task.FromResult(keys.GetValueOrDefault(deviceId));

        public Task<bool> RememberAssertionAsync(Guid deviceId, string assertionId, DateTimeOffset expiresAt, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Karry.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
