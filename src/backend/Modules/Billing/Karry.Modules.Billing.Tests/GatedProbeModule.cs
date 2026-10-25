using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Modules;
using Karry.SharedKernel.Entitlements;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Billing.Tests;

/// <summary>A module of the test host with one endpoint gated by the Maintenance feature (Professional and up).</summary>
internal sealed class GatedProbeModule : IModule
{
    private static readonly string[] WorkOrders = ["WO-1 crusher liner"];

    public string Name => "probe";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/work-orders", () => Results.Ok(new { items = WorkOrders }))
            .AllowAnyTenantMember()
            .RequiresFeature(Features.Maintenance)
            .WithName("ProbeWorkOrders");
}
