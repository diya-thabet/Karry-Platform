using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.BuildingBlocks.Authorization;

public static class AuthorizationExtensions
{
    /// <summary>The per-request caller access; the catalog comes from <c>AddModules</c> (empty without modules).</summary>
    public static IServiceCollection AddKarryAuthorization(this IServiceCollection services)
    {
        services.TryAddScoped<ICallerAccess, CallerAccessHolder>();
        services.TryAddSingleton(AuthorizationCatalog.Empty);
        return services;
    }

    /// <summary>The permission check; after tenant resolution (it needs the tenant), before idempotency.</summary>
    public static IApplicationBuilder UseKarryAuthorization(this IApplicationBuilder app) =>
        app.UseMiddleware<PermissionMiddleware>();
}
