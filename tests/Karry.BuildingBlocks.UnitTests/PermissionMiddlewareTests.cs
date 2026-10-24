using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// The permission check in front of every tenant endpoint (US-P1-IAM-05/06, TC-P1-IAM-009 step-up part). The caller,
/// tenant and sign-in time come from test headers; the roles from a fake <see cref="IAccessResolver"/>.
/// </summary>
[Trait("Story", "US-P1-IAM-06")]
public sealed class PermissionMiddlewareTests : IAsyncLifetime
{
    private static readonly Guid Tenant = Guid.Parse("01970000-0000-7000-8000-00000000000a");
    private static readonly Guid SiteA = Guid.Parse("01970000-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider clock = new(Now);
    private readonly Dictionary<string, IReadOnlyList<AccessAssignment>> roles = new(StringComparer.Ordinal)
    {
        ["admin"] = [Holding("probe.billing.manage", "probe.ticket.read")],
        ["weighmaster-a"] = [Holding("probe.ticket.read") with { SiteId = SiteA }],
    };

    private WebApplication? app;
    private HttpClient? client;
    private int handled;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryAuthorization();
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddSingleton(CallerAccessTests.Catalog);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IAccessResolver>(new Resolver(roles));
        app = builder.Build();
        app.UseKarryApiConventions();
        app.Use(FakeAuthentication);
        app.UseKarryAuthorization();

        var tenantEndpoints = app.MapGroup("/t").WithMetadata(new TenantRequired());
        tenantEndpoints.MapGet("/undeclared", Handle);
        tenantEndpoints.MapGet("/tickets", (ICallerAccess access) =>
        {
            handled++;
            return Results.Ok(new { sites = access.Current.ScopeOf("probe.ticket.read").Sites, all = access.Current.ScopeOf("probe.ticket.read").TenantWide });
        }).RequirePermission("probe.ticket.read");
        tenantEndpoints.MapGet("/billing", Handle).RequirePermission("probe.billing.manage");
        tenantEndpoints.MapGet("/both", Handle).RequirePermission("probe.ticket.read").RequirePermission("probe.billing.manage");
        tenantEndpoints.MapGet("/typo", Handle).RequirePermission("probe.biling.manage");
        tenantEndpoints.MapGet("/me", Handle).AllowAnyTenantMember();
        tenantEndpoints.MapPost("/billing/plan", Handle).RequirePermission("probe.billing.manage").RequireRecentSignIn();
        tenantEndpoints.MapGet("/operator", Handle).AllowWithoutTenant();

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

    [Fact]
    public async Task An_endpoint_that_declares_no_permission_is_refused_for_everyone()
    {
        var response = await SendAsync("GET", "/t/undeclared", "admin");

        await ExpectProblemAsync(response, HttpStatusCode.Forbidden, PermissionMiddleware.UndeclaredCode);
        handled.Should().Be(0);
    }

    [Theory]
    [InlineData("admin", "/t/billing", 200)]
    [InlineData("weighmaster-a", "/t/billing", 403)]
    [InlineData("weighmaster-a", "/t/tickets", 200)]
    [InlineData("unknown-subject", "/t/tickets", 403)]
    [InlineData(null, "/t/tickets", 403)]
    [InlineData("weighmaster-a", "/t/both", 403)]
    [InlineData("admin", "/t/both", 200)]
    public async Task The_caller_needs_every_permission_the_endpoint_names(string? subject, string path, int status)
    {
        var response = await SendAsync("GET", path, subject);

        ((int)response.StatusCode).Should().Be(status);
        handled.Should().Be(status == 200 ? 1 : 0);
        if (status == 403)
        {
            await ExpectProblemAsync(response, HttpStatusCode.Forbidden, PermissionMiddleware.ForbiddenCode);
        }
    }

    [Fact]
    public async Task A_misspelt_permission_refuses_the_endpoint_instead_of_opening_it()
    {
        var response = await SendAsync("GET", "/t/typo", "admin");

        await ExpectProblemAsync(response, HttpStatusCode.Forbidden, PermissionMiddleware.UndeclaredCode);
    }

    [Fact]
    public async Task The_endpoint_sees_the_scope_of_the_caller_for_its_filters()
    {
        var site = await (await SendAsync("GET", "/t/tickets", "weighmaster-a")).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var all = await (await SendAsync("GET", "/t/tickets", "admin")).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        site.GetProperty("all").GetBoolean().Should().BeFalse();
        site.GetProperty("sites").EnumerateArray().Select(s => s.GetGuid()).Should().Equal(SiteA);
        all.GetProperty("all").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Any_member_endpoints_and_endpoints_without_tenant_are_not_permission_checked()
    {
        (await SendAsync("GET", "/t/me", "unknown-subject")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync("GET", "/t/operator", subject: null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [Trait("TestCase", "TC-P1-IAM-009")]
    [InlineData(null, 401)]
    [InlineData(-301, 401)]
    [InlineData(-299, 200)]
    [InlineData(0, 200)]
    public async Task A_privileged_change_needs_a_sign_in_of_the_last_five_minutes(int? signedInSecondsAgo, int status)
    {
        var authTime = signedInSecondsAgo is { } offset ? Now.AddSeconds(offset).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        var response = await SendAsync("POST", "/t/billing/plan", "admin", authTime);

        ((int)response.StatusCode).Should().Be(status);
        if (status == 401)
        {
            await ExpectProblemAsync(response, HttpStatusCode.Unauthorized, PermissionMiddleware.StepUpCode);
            response.Headers.WwwAuthenticate.ToString().Should().Contain("error=\"insufficient_user_authentication\"").And.Contain("max_age=300");
            handled.Should().Be(0);
        }
    }

    [Theory]
    [Trait("TestCase", "TC-P1-IAM-009")]
    [InlineData("not-a-number")]
    [InlineData("-5")]
    [InlineData("99999999999999")]
    public async Task A_malformed_sign_in_time_counts_as_no_sign_in(string authTime)
    {
        var response = await SendAsync("POST", "/t/billing/plan", "admin", authTime);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Step_up_comes_after_the_permission_check_so_it_reveals_nothing_to_a_caller_without_the_permission()
    {
        var response = await SendAsync("POST", "/t/billing/plan", "weighmaster-a", Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        await ExpectProblemAsync(response, HttpStatusCode.Forbidden, PermissionMiddleware.ForbiddenCode);
    }

    private static AccessAssignment Holding(params string[] permissions) =>
        new("custom", permissions.ToHashSet(StringComparer.Ordinal), new Dictionary<string, FieldAccess>());

    private static async Task ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().Should().Be(code);
    }

    private static Task FakeAuthentication(HttpContext context, RequestDelegate next)
    {
        var claims = new List<Claim>();
        if (context.Request.Headers["X-Sub"].ToString() is { Length: > 0 } subject)
        {
            claims.Add(new Claim("sub", subject));
            context.RequestServices.GetRequiredService<ITenantContext>().Set(new TenantId(Tenant));
        }

        if (context.Request.Headers["X-Auth-Time"].ToString() is { Length: > 0 } authTime)
        {
            claims.Add(new Claim(PermissionMiddleware.AuthTimeClaim, authTime));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, claims.Count > 0 ? "test" : null));
        return next(context);
    }

    private IResult Handle()
    {
        handled++;
        return Results.Ok();
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? subject, string? authTime = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (subject is not null)
        {
            request.Headers.Add("X-Sub", subject);
        }

        if (authTime is not null)
        {
            request.Headers.Add("X-Auth-Time", authTime);
        }

        return await client!.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class Resolver(Dictionary<string, IReadOnlyList<AccessAssignment>> roles) : IAccessResolver
    {
        public ValueTask<IReadOnlyList<AccessAssignment>> AssignmentsAsync(TenantId tenant, string subject, CancellationToken cancellationToken) =>
            ValueTask.FromResult(tenant.Value == Tenant ? roles.GetValueOrDefault(subject) ?? [] : []);
    }
}
