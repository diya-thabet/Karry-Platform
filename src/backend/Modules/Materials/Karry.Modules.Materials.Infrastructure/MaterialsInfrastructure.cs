using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Materials.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Materials.Infrastructure;

public static class MaterialsInfrastructure
{
    /// <summary>Material storage and operations; the outbox delivers the module's events.</summary>
    public static IServiceCollection AddMaterialsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<MaterialsDbContext>(connectionString);
        services.AddOutboxDispatcher<MaterialsDbContext>(eventAssemblies: typeof(Domain.Material).Assembly);
        services.AddScoped<IMaterialStore, MaterialStore>();
        services.AddScoped<MaterialOperations>();
        services.AddScoped<DemoMaterialsSeeder>();
        services.AddScoped<IImportTarget, MaterialImport>();
        services.AddScoped<BuildingBlocks.Sync.ISyncSource, MaterialsSync>();
        return services;
    }
}
