using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Assets.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Assets.Infrastructure;

public static class AssetsInfrastructure
{
    /// <summary>Asset and meter storage and operations, the asset meter; the outbox delivers the module's events.</summary>
    public static IServiceCollection AddAssetsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<AssetsDbContext>(connectionString);
        services.AddOutboxDispatcher<AssetsDbContext>(eventAssemblies: typeof(Domain.Asset).Assembly);
        services.AddScoped<IAssetStore, AssetStore>();
        services.AddScoped<AssetOperations>();
        services.AddScoped<IMeterStore, MeterStore>();
        services.AddScoped<MeterOperations>();
        services.AddScoped<IUsageSource, CountedAssets>();
        return services;
    }
}
