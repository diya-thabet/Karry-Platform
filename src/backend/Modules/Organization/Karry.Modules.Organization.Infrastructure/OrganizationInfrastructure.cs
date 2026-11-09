using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Organization.Application;
using Karry.Modules.Organization.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Karry.Modules.Organization.Infrastructure;

public static class OrganizationInfrastructure
{
    /// <summary>Site storage (PostGIS), shifts and their generator, the site meter and the directory other modules read.</summary>
    public static IServiceCollection AddOrganizationInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<OrganizationDbContext>(connectionString);
        services.AddSingleton(new DataSourcePlugin(builder => builder.UseNetTopologySuite())); // geometries on the shared pool
        services.AddOutboxDispatcher<OrganizationDbContext>(eventAssemblies: typeof(Domain.Site).Assembly);
        services.AddScoped<ISiteStore, SiteStore>();
        services.AddScoped<SiteOperations>();
        services.AddScoped<IShiftStore, ShiftStore>();
        services.AddScoped<ShiftInstances>();
        services.AddScoped<ShiftOperations>();
        services.AddScoped<GenerateShiftInstances>();
        services.AddScoped<DemoSitesSeeder>();
        services.TryAddSingleton<ShiftMetrics>();
        services.AddScoped<IUsageSource, ActiveSites>();
        services.AddScoped<ISiteDirectory, SiteDirectory>();
        services.AddScoped<IShiftCalendar, ShiftResolution>();
        services.AddScoped<IImportTarget, SiteImport>();
        services.AddScoped<BuildingBlocks.Sync.ISyncSource, OrganizationSync>();
        return services;
    }
}
