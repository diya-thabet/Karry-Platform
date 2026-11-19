using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Sync;
using Karry.Modules.FieldOps.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.FieldOps.Infrastructure;

public static class FieldOpsInfrastructure
{
    /// <summary>Shift entries, checklists and safety holds, the handlers of the tablets' commands (ADR-0034, ADR-0035) and the meter states they pull.</summary>
    public static IServiceCollection AddFieldOpsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<FieldOpsDbContext>(connectionString);
        services.AddScoped<IShiftEntryStore, ShiftEntryStore>();
        services.AddScoped<IShiftEntryReader, ShiftEntryReader>();
        services.AddScoped<EntryReading>();
        services.AddScoped<IChecklistTemplateStore, ChecklistTemplateStore>();
        services.AddScoped<ChecklistTemplates>();
        services.AddScoped<IChecklistSubmissionStore, ChecklistSubmissionStore>();
        services.AddScoped<ISafetyHoldStore, SafetyHoldStore>();
        services.AddScoped<SafetyHolds>();
        services.AddScoped<ISyncSource, MeterStatesSync>();
        services.AddSyncCommandHandler<StartShiftEntry>(StartShiftEntry.Name);
        services.AddSyncCommandHandler<EndShiftEntry>(EndShiftEntry.Name);
        services.AddSyncCommandHandler<SubmitChecklist>(SubmitChecklist.Name);
        return services;
    }
}
