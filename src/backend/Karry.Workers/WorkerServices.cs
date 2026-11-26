using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Storage;
using Karry.Modules.Assets.Infrastructure;
using Karry.Modules.Billing.Infrastructure;
using Karry.Modules.FieldOps.Infrastructure;
using Karry.Modules.Flags.Infrastructure;
using Karry.Modules.Identity.Infrastructure;
using Karry.Modules.Imports.Infrastructure;
using Karry.Modules.Integrity.Infrastructure;
using Karry.Modules.Materials.Infrastructure;
using Karry.Modules.Notifications.Infrastructure;
using Karry.Modules.Organization.Infrastructure;
using Karry.Modules.Sync.Infrastructure;
using Karry.Modules.Tenancy.Infrastructure;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Infrastructure;
using Karry.Modules.Verification.Infrastructure;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// The composition of this host, shared by <c>Program</c> and the start-up tests: a registration missing here fails a
/// test (the container is validated as in Development) instead of a deployment.
/// </summary>
internal static class WorkerServices
{
    /// <summary>The modules' infrastructure whose work this host runs (outbox subscribers, scheduled runs).</summary>
    public static IServiceCollection AddWorkerModules(this IServiceCollection services, IConfiguration configuration)
    {
        // An empty value (an unset deployment variable) is refused like a missing one, before any module uses it.
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Karry is required.");
        }

        services.AddTenancyInfrastructure(connectionString);
        services.AddTrustInfrastructure(connectionString);
        services.AddTrustAnchoring(configuration);
        services.AddTrustServerSigning(configuration); // countersigning (ADR-0033 §4)
        services.AddIdentityInfrastructure(configuration, connectionString);

        // Billing subscribes to tenant provisioning (the trial subscription) and runs the subscription lifecycle.
        services.AddBillingInfrastructure(connectionString);

        // Flags: delivers its events (company flag changes); the evaluation itself runs in the API.
        services.AddFlagsInfrastructure(connectionString);

        // Organization: delivers its events (site and shift changes) and generates the shift instances (daily job).
        services.AddOrganizationInfrastructure(connectionString);

        // Materials: delivers its events (material and density changes).
        services.AddMaterialsInfrastructure(connectionString);

        // Assets: delivers its events (asset registrations, changes, moves).
        services.AddAssetsInfrastructure(connectionString, configuration);

        // Sync: measures the sequence gaps of tablets (5-minute job, alert KarrySyncGapOpen).
        services.AddSyncInfrastructure(connectionString);

        // FieldOps and Integrity: a meter event dispatched by the Assets outbox re-evaluates the meter's continuity here
        // (IMeterEventSubscriber, ADR-0036 §4), raising and closing integrity flags in the dispatcher's transaction.
        services.AddFieldOpsInfrastructure(connectionString);
        // FieldOps' reading of entries reads their photos (ADR-0038 §2): the evidence store, unavailable without configuration.
        services.AddEvidenceStore(configuration);
        services.AddIntegrityInfrastructure(connectionString);

        // Verification: the FieldOps handlers submit ended entries for review in their transaction (ADR-0037 §1); the SLA job
        // of P2.8 runs here too.
        services.AddVerificationInfrastructure(connectionString);
        // Notifications (ADR-0039): the FieldOps, Verification and Integrity outboxes hand their notices to it.
        services.AddNotificationsInfrastructure(connectionString);

        // Imports: validates and commits uploaded files (its outbox runs the import runner) and deletes files after 90 days.
        // The runner rebuilds the requester's access, so this host knows every module's permissions.
        services.AddSingleton(WorkerModules.Catalog);
        services.AddImportsInfrastructure(connectionString, configuration);
        return services;
    }

    /// <summary>Whether ledger anchoring is configured (<c>Trust:Anchoring:Bucket</c>), which registers its runner.</summary>
    public static bool HasAnchoring(this IServiceCollection services) => services.Any(s => s.ServiceType == typeof(AnchorLedgers));

    /// <summary>Whether Keycloak is configured, whose failed sign-ins are imported every minute.</summary>
    public static bool HasKeycloak(IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration["Identity:Keycloak:BaseUrl"]);

    /// <summary>
    /// The long-running part: every module's outbox dispatcher (ADR-0015; Tenancy's tenant_provisioned sets up the tenant
    /// in Keycloak and starts its trial subscription in Billing) and the scheduled jobs (Quartz, ADR-0018).
    /// </summary>
    public static IServiceCollection AddWorkerJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOutboxDispatcherService<TenancyDbContext>();
        services.AddOutboxDispatcherService<IdentityDbContext>();
        services.AddOutboxDispatcherService<BillingDbContext>();
        services.AddOutboxDispatcherService<FlagsDbContext>();
        services.AddOutboxDispatcherService<OrganizationDbContext>();
        services.AddOutboxDispatcherService<MaterialsDbContext>();
        services.AddOutboxDispatcherService<AssetsDbContext>();
        services.AddOutboxDispatcherService<ImportsDbContext>();
        services.AddOutboxDispatcherService<TrustDbContext>();
        services.AddOutboxDispatcherService<FieldOpsDbContext>();
        services.AddOutboxDispatcherService<VerificationDbContext>();
        services.AddOutboxDispatcherService<IntegrityDbContext>();
        services.AddOutboxDispatcherService<NotificationsDbContext>();

        var anchoring = services.HasAnchoring();
        var keycloak = HasKeycloak(configuration);
        services.AddQuartz(quartz =>
        {
            foreach (var job in Jobs(anchoring, keycloak))
            {
                job.Add(quartz);
            }
        });
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
        return services;
    }

    /// <summary>The scheduled jobs of this host: anchoring only when configured, the sign-in import only with Keycloak.</summary>
    public static IReadOnlyList<ScheduledJob> Jobs(bool anchoring, bool keycloak) =>
    [
        ScheduledJob.Of<SubscriptionLifecycleJob>("subscription-lifecycle", SubscriptionLifecycleJob.Schedule),
        ScheduledJob.Of<UsageSnapshotJob>("usage-snapshot", UsageSnapshotJob.Schedule),
        ScheduledJob.Of<ShiftInstanceJob>("shift-instances", ShiftInstanceJob.Schedule),
        ScheduledJob.Of<ImportRetentionJob>("import-retention", ImportRetentionJob.Schedule),
        ScheduledJob.Of<SyncGapJob>("sync-gaps", SyncGapJob.Schedule),
        ScheduledJob.Of<ReviewEscalationJob>("review-escalation", ReviewEscalationJob.Schedule),
        ScheduledJob.Of<UnsettledGapsJob>("unsettled-gaps", UnsettledGapsJob.Schedule),
        ScheduledJob.Of<MissingEndJob>("missing-ends", MissingEndJob.Schedule),
        ScheduledJob.Of<DigestJob>("digests", DigestJob.Schedule),
        ScheduledJob.Of<EmailDeliveryJob>("email-deliveries", EmailDeliveryJob.Schedule),
        ScheduledJob.Of<PushDeliveryJob>("push-deliveries", PushDeliveryJob.Schedule),
        ScheduledJob.Of<NoticeRetentionJob>("notice-retention", NoticeRetentionJob.Schedule),
        ScheduledJob.Of<CountersignJob>("countersign", CountersignJob.Schedule),
        .. anchoring ? [ScheduledJob.Of<LedgerAnchoringJob>("ledger-anchoring", LedgerAnchoringJob.Schedule)] : Array.Empty<ScheduledJob>(),
        .. keycloak ? [ScheduledJob.Of<SignInFailureImportJob>("sign-in-failure-import", SignInFailureImportJob.Schedule)] : Array.Empty<ScheduledJob>(),
    ];
}

/// <summary>
/// A job with its cron schedule plus one run at start-up (catch-up after downtime; the staleness alerts read the first
/// run's time). Two triggers: a cron trigger's first fire is its next cron time, even with StartNow().
/// </summary>
internal sealed record ScheduledJob(Type JobType, string Name, string Cron, Action<IQuartzBuilder, JobKey> AddJob)
{
    public static ScheduledJob Of<TJob>(string name, string cron)
        where TJob : IJob =>
        new(typeof(TJob), name, cron, (quartz, key) => quartz.AddJob<TJob>(options => options.WithIdentity(key)));

    public void Add(IQuartzBuilder quartz)
    {
        var job = new JobKey(Name);
        AddJob(quartz, job);
        quartz.AddTrigger(trigger => trigger.ForJob(job).WithIdentity($"{Name}-schedule").WithCronSchedule(Cron));
        quartz.AddTrigger(trigger => trigger.ForJob(job).WithIdentity($"{Name}-start-up").StartNow());
    }
}
