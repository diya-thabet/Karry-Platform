using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Sync.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Sync.Infrastructure;

public static class SyncInfrastructure
{
    /// <summary>The inbox of uploaded commands, and the measurement of sequence gaps (a Workers job).</summary>
    public static IServiceCollection AddSyncInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddModuleDbContext<SyncDbContext>(connectionString);
        services.AddScoped<IInboxStore, InboxStore>();
        services.AddScoped<IWaitingReport, WaitingReport>();
        services.AddScoped<BuildingBlocks.Sync.ISyncSource, SubmissionsSync>();
        services.AddScoped<MeasureSyncGaps>();
        services.TryAddSingleton<SyncGapMetrics>();
        services.TryAddSingleton<SyncBlobMetrics>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
