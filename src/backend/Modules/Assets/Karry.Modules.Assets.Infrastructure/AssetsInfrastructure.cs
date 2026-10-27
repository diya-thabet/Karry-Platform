using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Storage;
using Karry.Modules.Assets.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Assets.Infrastructure;

public static class AssetsInfrastructure
{
    /// <summary>Asset and meter storage and operations, photos in object storage, the asset meter, the asset and meter imports; the outbox delivers the module's events.</summary>
    public static IServiceCollection AddAssetsInfrastructure(this IServiceCollection services, string connectionString, IConfiguration configuration)
    {
        services.AddObjectStore(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<AssetsDbContext>(connectionString);
        services.AddOutboxDispatcher<AssetsDbContext>(eventAssemblies: typeof(Domain.Asset).Assembly);
        services.AddScoped<IAssetStore, AssetStore>();
        services.AddScoped<AssetOperations>();
        services.AddSingleton<ILabelSheet, LabelSheet>();
        services.AddScoped<IMeterStore, MeterStore>();
        services.AddScoped<MeterOperations>();
        services.AddScoped<DemoAssetsSeeder>();
        services.AddScoped<IUsageSource, CountedAssets>();

        // Excel import of machines and meter readings (P1.14): run by the Imports module, with these rules.
        services.AddScoped<IImportTarget, AssetImport>();
        services.AddScoped<IImportTarget, MeterImport>();
        return services;
    }
}
