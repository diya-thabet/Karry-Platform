using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Organization.Application;
using Karry.Modules.Organization.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Organization.Infrastructure;

public static class OrganizationInfrastructure
{
    /// <summary>Site storage (PostGIS), the site meter and the directory other modules read.</summary>
    public static IServiceCollection AddOrganizationInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<OrganizationDbContext>(connectionString);
        services.AddOutboxDispatcher<OrganizationDbContext>(eventAssemblies: typeof(Domain.Site).Assembly);
        services.AddScoped<ISiteStore, SiteStore>();
        services.AddScoped<SiteOperations>();
        services.AddScoped<IUsageSource, ActiveSites>();
        services.AddScoped<ISiteDirectory, SiteDirectory>();
        return services;
    }
}
