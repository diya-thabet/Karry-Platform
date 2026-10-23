using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// Tenant resolution from the validated token (US-P1-TEN-02 AC2). A request that cannot be tied to exactly one tenant
/// is rejected before the endpoint runs and <b>before any database connection is opened</b> (TC-P1-TEN-003).
/// </summary>
[Trait("Story", "US-P1-TEN-02")]
public sealed class TenantResolutionTests : IAsyncLifetime
{
    private const string Issuer = "https://auth.karry.test/realms/karry";
    private const string Audience = "karry-api";
    private static readonly Guid TenantA = Guid.Parse("01970000-0000-7000-8000-00000000000a");

    private readonly ConnectionCounter connections = new();
    private WebApplication? app;
    private HttpClient? client;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryTenancy(builder.Configuration);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = null;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = SharedKey,
            };
        });
        builder.Services.AddSingleton(connections);
        // Port 1 never answers: a request that reaches the database fails, and the counter sees the attempt.
        builder.Services.AddModuleDbContext<CountingDbContext>("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
        builder.Services.AddModules(builder.Configuration, [new ProbeModule()]);
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
        app.MapModules();

        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    public static TheoryData<string, string?, int, string> RejectedRequests => new()
    {
        { "no token", null, 401, "" },
        { "token without tenant claim", Token(), 403, "tenant.missing" },
        { "tenant claim is not a UUID", Token(("tenant_id", "acme")), 403, "tenant.missing" },
        { "tenant claim is the empty UUID", Token(("tenant_id", Guid.Empty.ToString())), 403, "tenant.missing" },
        { "two tenant claims", Token(("tenant_id", TenantA.ToString()), ("tenant_id", Guid.CreateVersion7().ToString())), 403, "tenant.missing" },
        { "token signed by another key", Token(new RsaSecurityKey(RSA.Create(2048)), ("tenant_id", TenantA.ToString())), 401, "" },
        { "expired token", Token(expires: DateTime.UtcNow.AddMinutes(-10), claims: ("tenant_id", TenantA.ToString())), 401, "" },
    };

    [Theory]
    [MemberData(nameof(RejectedRequests))]
    [Trait("TestCase", "TC-P1-TEN-003")]
    public async Task A_request_without_exactly_one_valid_tenant_is_rejected_before_any_database_access(
        string scenario, string? token, int status, string code)
    {
        var response = await SendAsync("/api/v1/probe/notes", token);

        ((int)response.StatusCode).Should().Be(status, scenario);
        if (code.Length > 0)
        {
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
            problem.GetProperty("code").GetString().Should().Be(code);
        }

        connections.Opened.Should().Be(0, $"{scenario}: no database connection may be opened");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-003")]
    public async Task With_a_valid_tenant_the_endpoint_runs_and_reaches_the_database()
    {
        // Positive control: proves the counter really sees database access, so the zeros above mean something.
        var response = await SendAsync("/api/v1/probe/notes", Token(("tenant_id", TenantA.ToString())));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the test database is unreachable on purpose");
        connections.Opened.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_tenant_of_the_token_becomes_the_tenant_of_the_unit_of_work()
    {
        var response = await SendAsync("/api/v1/probe/whoami", Token(("tenant_id", TenantA.ToString())));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(TenantA.ToString());
    }

    [Fact]
    public async Task An_endpoint_that_opts_out_works_without_a_tenant_but_still_needs_authentication()
    {
        (await SendAsync("/api/v1/probe/platform", Token())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync("/api/v1/probe/platform", token: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client!.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The issuer's key, trusted by the host (static: theory data is built before the test instance).</summary>
    private static readonly RsaSecurityKey SharedKey = new(RSA.Create(2048)) { KeyId = "issuer" };

    private static string Token(params (string Type, string Value)[] claims) => Token(SharedKey, claims);

    private static string Token(SecurityKey key, params (string Type, string Value)[] claims) => Token(key, DateTime.UtcNow.AddMinutes(5), claims);

    private static string Token(DateTime expires, params (string Type, string Value)[] claims) => Token(SharedKey, expires, claims);

    private static string Token(SecurityKey key, DateTime expires, (string Type, string Value)[] claims) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            NotBefore = expires.AddMinutes(-30),
            IssuedAt = expires.AddMinutes(-30),
            Expires = expires,
            Subject = new ClaimsIdentity([new Claim("sub", "user-1"), .. claims.Select(c => new Claim(c.Type, c.Value))]),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });

    public sealed class ConnectionCounter : DbConnectionInterceptor
    {
        private int opened;

        public int Opened => Volatile.Read(ref opened);

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref opened);
            return ValueTask.FromResult(result);
        }
    }

    public sealed class CountingDbContext(DbContextOptions<CountingDbContext> options, ConnectionCounter counter) : ModuleDbContext(options)
    {
        public override string Schema => "probe";

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.AddInterceptors(counter);
        }

        protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
        {
        }
    }

    private sealed class ProbeModule : IModule
    {
        public string Name => "probe";

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/notes", async (CountingDbContext db) => await db.OutboxMessages.CountAsync());
            endpoints.MapGet("/whoami", (ITenantContext tenant) => tenant.Current?.ToString());
            endpoints.MapGet("/platform", () => "ok").AllowWithoutTenant();
        }
    }
}
