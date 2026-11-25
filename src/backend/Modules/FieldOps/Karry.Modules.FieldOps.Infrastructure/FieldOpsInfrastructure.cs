using Karry.BuildingBlocks.Outbox;
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
        // Its events reach other modules (notices, ADR-0039): dispatched by the Workers.
        services.AddOutboxDispatcher<FieldOpsDbContext>(eventAssemblies: typeof(Domain.ShiftEntryVersion).Assembly);
        services.AddScoped<IShiftEntryStore, ShiftEntryStore>();
        services.AddScoped<IShiftEntryReader, ShiftEntryReader>();
        services.AddScoped<EntryReading>();
        services.AddScoped<IOpenSegments, OpenSegments>();
        services.AddScoped<BuildingBlocks.Outbox.IDomainEventSubscriber<Domain.AssetMarkedUnsafe>, HoldNotices>();
        services.AddScoped<MissingEndCheck>();
        services.AddSingleton<RaiseMissingEnds>();
        services.AddScoped<IChecklistTemplateStore, ChecklistTemplateStore>();
        services.AddScoped<ChecklistTemplates>();
        services.AddScoped<IChecklistSubmissionStore, ChecklistSubmissionStore>();
        services.AddScoped<ISafetyHoldStore, SafetyHoldStore>();
        services.AddScoped<SafetyHolds>();
        services.AddScoped<DemoChecklistsSeeder>();
        services.AddScoped<Assets.Contracts.IMeterEventSubscriber, MeterEventContinuity>();
        services.AddScoped<Verification.Contracts.IReviewableRecords, ShiftEntryReviewFacts>();
        services.AddScoped<Integrity.Contracts.IFlagParticipants, FlagParticipants>();
        services.AddScoped<ISyncSource, MeterStatesSync>();
        services.AddScoped<ISyncSource, ChecklistsSync>();
        services.AddSyncCommandHandler<StartShiftEntry>(StartShiftEntry.Name);
        services.AddSyncCommandHandler<EndShiftEntry>(EndShiftEntry.Name);
        services.AddSyncCommandHandler<SubmitChecklist>(SubmitChecklist.Name);
        return services;
    }
}
