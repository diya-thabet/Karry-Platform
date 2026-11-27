using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Sales.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Sales.Infrastructure;

public static class SalesInfrastructure
{
    /// <summary>Customer storage and operations; the outbox delivers the module's events.</summary>
    public static IServiceCollection AddSalesInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<SalesDbContext>(connectionString);
        services.AddOutboxDispatcher<SalesDbContext>(eventAssemblies: typeof(Domain.Customer).Assembly);
        services.AddScoped<ICustomerStore, CustomerStore>();
        services.AddScoped<CustomerOperations>();
        return services;
    }
}
