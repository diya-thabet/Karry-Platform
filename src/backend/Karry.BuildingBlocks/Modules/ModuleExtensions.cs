using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.Modules;

public static class ModuleExtensions
{
    /// <summary>Registers every module's services, the module list (for endpoint mapping) and their authorization catalog.</summary>
    public static IServiceCollection AddModules(
        this IServiceCollection services, IConfiguration configuration, IReadOnlyList<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var duplicate = modules.GroupBy(m => m.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Module name '{duplicate.Key}' is registered more than once.");
        }

        foreach (var module in modules)
        {
            module.AddServices(services, configuration);
        }

        services.AddSingleton(modules);
        // Validated here so a wrong declaration stops the host at start-up, not at the first request.
        services.AddSingleton(AuthorizationCatalog.From(modules));
        return services;
    }

    /// <summary>
    /// Maps each module's endpoints under <c>/api/v1/{module}</c>, tagged with <see cref="ModuleMetadata"/>, requiring
    /// authentication and a tenant by default.
    /// </summary>
    public static IEndpointRouteBuilder MapModules(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var modules = endpoints.ServiceProvider.GetRequiredService<IReadOnlyList<IModule>>();
        foreach (var module in modules)
        {
            // Secure by default: every module endpoint needs an authenticated caller and a tenant, unless an endpoint
            // explicitly opts out (.AllowAnonymous(), .AllowWithoutTenant()); sensitive inputs need the Write level.
            var group = endpoints.MapGroup($"/api/v1/{module.Name}")
                .WithMetadata(new ModuleMetadata(module.Name), new TenantRequired())
                .RequireAuthorization()
                .AddEndpointFilterFactory(SensitiveInputFilter.Create)
                .AddEndpointFilter(StepUp.ChallengeFilter);
            module.MapEndpoints(group);
        }

        return endpoints;
    }
}
