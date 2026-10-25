using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Billing.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Billing.Infrastructure;

public static class BillingInfrastructure
{
    public static IServiceCollection AddBillingInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddModuleDbContext<BillingDbContext>(connectionString);
        services.AddOutboxDispatcher<BillingDbContext>(eventAssemblies: typeof(Domain.Plan).Assembly);
        services.AddScoped<IPlanStore, PlanStore>();
        services.AddScoped<ISubscriptionStore, SubscriptionStore>();
        services.AddScoped<SubscriptionOperations>();
        services.AddScoped<Tenancy.Contracts.ITenantProvisionedHandler, TrialOnProvisioning>();
        services.AddSingleton<AdvanceSubscriptions>();
        services.AddMemoryCache();
        services.AddSingleton<EntitlementCacheGenerations>();
        services.AddSingleton<IEntitlementCache>(sp => sp.GetRequiredService<EntitlementCacheGenerations>());
        services.AddScoped<BuildingBlocks.Entitlements.IEntitlementResolver, EntitlementResolver>();
        return services;
    }
}
