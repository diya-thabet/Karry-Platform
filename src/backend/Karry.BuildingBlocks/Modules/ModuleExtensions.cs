using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.Modules;

public static class ModuleExtensions
{
    /// <summary>Registers every module's services and the module list itself (for endpoint mapping).</summary>
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
        return services;
    }

    /// <summary>Maps each module's endpoints under <c>/api/v1/{module}</c>.</summary>
    public static IEndpointRouteBuilder MapModules(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var modules = endpoints.ServiceProvider.GetRequiredService<IReadOnlyList<IModule>>();
        foreach (var module in modules)
        {
            module.MapEndpoints(endpoints.MapGroup($"/api/v1/{module.Name}"));
        }

        return endpoints;
    }
}
