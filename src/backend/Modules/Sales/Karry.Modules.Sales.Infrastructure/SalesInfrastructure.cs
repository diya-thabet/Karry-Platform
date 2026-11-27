using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Sales.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Sales.Infrastructure;

public static class SalesInfrastructure
{
    /// <summary>Customers, products and price lists: storage and operations, and the price book other modules read; the outbox delivers the module's events.</summary>
    public static IServiceCollection AddSalesInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<SalesDbContext>(connectionString);
        services.AddOutboxDispatcher<SalesDbContext>(eventAssemblies: typeof(Domain.Customer).Assembly);
        services.AddScoped<ICustomerStore, CustomerStore>();
        services.AddScoped<CustomerOperations>();
        services.AddScoped<IProductStore, ProductStore>();
        services.AddScoped<ProductOperations>();
        services.AddScoped<IPriceListStore, PriceListStore>();
        services.AddScoped<PriceListOperations>();
        services.AddScoped<Contracts.IPriceBook>(sp => sp.GetRequiredService<PriceListOperations>());
        services.AddScoped<BuildingBlocks.Imports.IImportTarget, CustomerImport>();
        services.AddScoped<BuildingBlocks.Imports.IImportTarget, ProductImport>();
        return services;
    }
}
