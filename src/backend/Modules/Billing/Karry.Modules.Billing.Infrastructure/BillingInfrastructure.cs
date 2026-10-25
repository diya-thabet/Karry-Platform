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
        return services;
    }
}
