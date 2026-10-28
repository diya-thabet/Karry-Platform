using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Sync.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Sync.Infrastructure;

public static class SyncInfrastructure
{
    /// <summary>The inbox of uploaded commands.</summary>
    public static IServiceCollection AddSyncInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddModuleDbContext<SyncDbContext>(connectionString);
        services.AddScoped<IInboxStore, InboxStore>();
        return services;
    }
}
