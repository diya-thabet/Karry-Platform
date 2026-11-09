using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Sync;
using Karry.Modules.FieldOps.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.FieldOps.Infrastructure;

public static class FieldOpsInfrastructure
{
    /// <summary>Shift entry storage and the handlers of the tablets' shift commands (ADR-0034).</summary>
    public static IServiceCollection AddFieldOpsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<FieldOpsDbContext>(connectionString);
        services.AddScoped<IShiftEntryStore, ShiftEntryStore>();
        services.AddSyncCommandHandler<StartShiftEntry>(StartShiftEntry.Name);
        services.AddSyncCommandHandler<EndShiftEntry>(EndShiftEntry.Name);
        return services;
    }
}
