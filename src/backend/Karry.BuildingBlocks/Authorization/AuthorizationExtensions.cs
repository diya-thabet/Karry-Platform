using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.BuildingBlocks.Authorization;

public static class AuthorizationExtensions
{
    /// <summary>
    /// The per-request caller access and field masking of JSON responses; the catalog comes from <c>AddModules</c>
    /// (empty without modules).
    /// </summary>
    public static IServiceCollection AddKarryAuthorization(this IServiceCollection services)
    {
        services.TryAddScoped<ICallerAccess, CallerAccessHolder>();
        services.TryAddSingleton(AuthorizationCatalog.Empty);
        if (!services.Any(d => d.ServiceType == typeof(MaskingRegistered)))
        {
            services.AddSingleton<MaskingRegistered>();
            services.AddKarryFieldMasking();
        }

        return services;
    }

    private sealed class MaskingRegistered;

    /// <summary>
    /// The permission check, then the plan check, then the subscription state (ADR-0023); after tenant resolution (they
    /// need the tenant), before idempotency.
    /// </summary>
    public static IApplicationBuilder UseKarryAuthorization(this IApplicationBuilder app) =>
        app.UseMiddleware<PermissionMiddleware>().UseMiddleware<Entitlements.EntitlementMiddleware>().UseMiddleware<Entitlements.SubscriptionStateMiddleware>();
}
