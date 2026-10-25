using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// The plan check of gated endpoints (US-P1-BIL-03 AC1, TC-P1-BIL-002, ADR-0023): after the permission check, fail
/// closed, unknown feature codes refused at mapping time. The tenant's features come from a fake resolver.
/// </summary>
[Trait("Story", "US-P1-BIL-03")]
public sealed class EntitlementMiddlewareTests : IAsyncLifetime
{
    private static readonly Guid Tenant = Guid.Parse("01970000-0000-7000-8000-00000000000b");

    private readonly HashSet<string> features = new(StringComparer.Ordinal) { Features.FieldOperations };
    private WebApplication? app;
    private HttpClient? client;
    private int handled;

    public async ValueTask InitializeAsync() => await StartAsync(withResolver: true);

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    [Trait("TestCase", "TC-P1-BIL-002")]
    public async Task A_gated_endpoint_answers_feature_not_in_plan_with_the_feature_and_no_data()
    {
        var response = await client!.GetAsync("/t/maintenance", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().Should().Be(EntitlementMiddleware.FeatureNotInPlanCode);
        problem.GetProperty("feature").GetString().Should().Be(Features.Maintenance);
        handled.Should().Be(0, "the endpoint never runs");
    }

    [Fact]
    public async Task An_included_feature_reaches_the_endpoint()
    {
        features.Add(Features.Maintenance);

        var response = await client!.GetAsync("/t/maintenance", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handled.Should().Be(1);
    }

    [Fact]
    public async Task The_permission_is_checked_before_the_plan()
    {
        var response = await client!.GetAsync("/t/undeclared-and-gated", TestContext.Current.CancellationToken);

        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("code").GetString()
            .Should().Be(PermissionMiddleware.UndeclaredCode, "a caller without the permission learns nothing about the plan");
    }

    [Fact]
    public async Task Without_a_resolver_a_gated_endpoint_is_refused()
    {
        await DisposeAsync();
        await StartAsync(withResolver: false);
        features.Add(Features.Maintenance);

        var response = await client!.GetAsync("/t/maintenance", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        handled.Should().Be(0);
    }

    [Fact]
    public void An_unknown_feature_code_fails_when_the_endpoint_is_mapped()
    {
        var builder = WebApplication.CreateBuilder();
        using var host = builder.Build();

        var act = () => host.MapGet("/x", () => "x").RequiresFeature("maintenence");

        act.Should().Throw<ArgumentException>().WithMessage("*maintenence*");
    }

    private async Task StartAsync(bool withResolver)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryAuthorization();
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddSingleton<IAccessResolver>(new EveryoneHoldsEverything());
        if (withResolver)
        {
            builder.Services.AddSingleton<IEntitlementResolver>(new Resolver(features));
        }

        app = builder.Build();
        app.UseKarryApiConventions();
        app.Use(SignedIn);
        app.UseKarryAuthorization();

        var tenantEndpoints = app.MapGroup("/t").WithMetadata(new TenantRequired());
        tenantEndpoints.MapGet("/maintenance", Handle).AllowAnyTenantMember().RequiresFeature(Features.Maintenance);
        tenantEndpoints.MapGet("/undeclared-and-gated", Handle).RequiresFeature(Features.Maintenance);

        await app.StartAsync();
        client = app.GetTestClient();
    }

    private IResult Handle()
    {
        handled++;
        return Results.Ok(new { secret = "maintenance data" });
    }

    private static Task SignedIn(HttpContext context, RequestDelegate next)
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test"));
        context.RequestServices.GetRequiredService<ITenantContext>().Set(new TenantId(Tenant));
        return next(context);
    }

    private sealed class Resolver(HashSet<string> features) : IEntitlementResolver
    {
        public ValueTask<TenantEntitlements> ResolveAsync(TenantId tenant, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new TenantEntitlements("professional", 1, "active", null, features, new Dictionary<string, int>(), 1, 0));
    }

    private sealed class EveryoneHoldsEverything : IAccessResolver
    {
        public ValueTask<IReadOnlyList<AccessAssignment>> AssignmentsAsync(TenantId tenant, string subject, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AccessAssignment>>([]);
    }
}
