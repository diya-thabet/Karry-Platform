using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Entitlements;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Billing.Tests;

/// <summary>
/// A module of the test host standing in for modules not built yet: one endpoint gated by the Maintenance feature
/// (Professional and up), and a counted "asset" with a guarded creation and an unguarded field capture (soft limits).
/// </summary>
internal sealed class GatedProbeModule : IModule
{
    private static readonly string[] WorkOrders = ["WO-1 crusher liner"];

    /// <summary>Assets per tenant, set directly by tests or created through the API.</summary>
    public static ConcurrentDictionary<Guid, long> Assets { get; } = new();

    public string Name => "probe";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IUsageSource, ProbeAssets>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/work-orders", () => Results.Ok(new { items = WorkOrders }))
            .AllowAnyTenantMember()
            .RequiresFeature(Features.Maintenance)
            .WithName("ProbeWorkOrders");

        endpoints.MapPost("/assets", CreateAssetAsync).AllowAnyTenantMember().WithName("ProbeCreateAsset");

        // Field capture: never limited, never calls the guard; keeps working while the subscription is restricted.
        endpoints.MapPost("/field-readings", () => Results.Ok(new { accepted = true })).AllowAnyTenantMember().FieldCapture().WithName("ProbeFieldReading");
    }

    private static async Task<IResult> CreateAssetAsync([FromServices] ILimitGuard limits, [FromServices] ITenantContext tenant, CancellationToken cancellationToken)
    {
        if (await limits.CheckCreateAsync(Metrics.Assets, cancellationToken) is { } refused)
        {
            return refused.ToProblem();
        }

        return Results.Ok(new { count = Assets.AddOrUpdate(tenant.Current!.Value.Value, 1, (_, count) => count + 1) });
    }

    private sealed class ProbeAssets(ITenantContext tenant) : IUsageSource
    {
        public string Metric => Metrics.Assets;

        public Task<long> CountAsync(CancellationToken cancellationToken) => Task.FromResult(Assets.GetValueOrDefault(tenant.Current!.Value.Value));
    }
}
