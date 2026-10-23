using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Tenancy.Infrastructure;

public static class TenancyInfrastructure
{
    public static IServiceCollection AddTenancyInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddModuleDbContext<TenancyDbContext>(connectionString);
        services.AddOutboxDispatcher<TenancyDbContext>(eventAssemblies: typeof(Domain.Tenant).Assembly);
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<Contracts.ITenantDirectory, TenantDirectory>();
        services.AddMemoryCache();
        services.AddSingleton<ITenantSlugLookup, TenantSlugLookup>();
        return services;
    }
}
