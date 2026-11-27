using Karry.BuildingBlocks.Observability;
using Karry.BuildingBlocks.Outbox;
using Karry.Modules.Assets.Infrastructure;
using Karry.Modules.Billing.Application;
using Karry.Modules.Billing.Infrastructure;
using Karry.Modules.FieldOps.Infrastructure;
using Karry.Modules.Flags.Infrastructure;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Infrastructure;
using Karry.Modules.Imports.Application;
using Karry.Modules.Imports.Infrastructure;
using Karry.Modules.Integrity.Infrastructure;
using Karry.Modules.Materials.Infrastructure;
using Karry.Modules.Notifications.Infrastructure;
using Karry.Modules.Organization.Application;
using Karry.Modules.Organization.Infrastructure;
using Karry.Modules.Sales.Infrastructure;
using Karry.Modules.Sync.Application;
using Karry.Modules.Tenancy.Infrastructure;
using Karry.Modules.Trust.Application;
using Karry.Modules.Verification.Infrastructure;
using Karry.Workers;

var builder = Host.CreateApplicationBuilder(args);
builder.AddKarryObservability("karry-workers");
builder.Services.AddWorkerModules(builder.Configuration);

var anchoring = builder.Services.HasAnchoring();

// `Karry.Workers anchor-now`: one anchoring run, then exit (manual catch-up, smoke test). Exit 1 if a tenant failed.
if (args is ["anchor-now", ..])
{
    if (!anchoring)
    {
        await Console.Error.WriteLineAsync("Trust:Anchoring:Bucket is not configured.").ConfigureAwait(false);
        return 2;
    }

    using var once = builder.Build();
    var run = await once.Services.GetRequiredService<AnchorLedgers>().RunAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"anchoring: {run.Tenants} tenants, {run.Anchored} anchored, {run.Failed} failed");
    return run.Failed == 0 ? 0 : 1;
}

// `Karry.Workers dispatch-now`: drain every module's outbox once, then exit (catch-up, smoke test). Exit 1 if
// messages are left (a subscriber failed and is waiting for its retry).
if (args is ["dispatch-now", ..])
{
    using var once = builder.Build();
    var left = 0;
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<TenancyDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<IdentityDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<BillingDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<FlagsDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<OrganizationDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<MaterialsDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<SalesDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<AssetsDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<ImportsDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<FieldOpsDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<VerificationDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<IntegrityDbContext>>()).ConfigureAwait(false);
    left += await DrainAsync(once.Services.GetRequiredService<OutboxDispatcher<NotificationsDbContext>>()).ConfigureAwait(false);
    Console.WriteLine($"outbox: {left} message(s) left for a retry");
    return left == 0 ? 0 : 1;
}

// `Karry.Workers advance-subscriptions`: one run of the subscription time rules, then exit. Exit 1 if a tenant failed.
if (args is ["advance-subscriptions", ..])
{
    using var once = builder.Build();
    var run = await once.Services.GetRequiredService<AdvanceSubscriptions>().RunAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"subscriptions: {run.Tenants} tenants, {run.Changed} changed, {run.Failed} failed");
    return run.Failed == 0 ? 0 : 1;
}

// `Karry.Workers snapshot-usage`: one usage snapshot of every tenant, then exit. Exit 1 if a tenant failed.
if (args is ["snapshot-usage", ..])
{
    using var once = builder.Build();
    var run = await once.Services.GetRequiredService<SnapshotUsage>().RunAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"usage: {run.Tenants} tenants, {run.OverLimit} metric(s) over their limit, {run.Failed} failed");
    return run.Failed == 0 ? 0 : 1;
}

// `Karry.Workers measure-sync-gaps`: one measurement of the tablets' sequence gaps, then exit. Exit 1 if a tenant
// failed or a tablet's gap is open for over 24 hours.
if (args is ["measure-sync-gaps", ..])
{
    using var once = builder.Build();
    var run = await once.Services.GetRequiredService<MeasureSyncGaps>().RunAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"sync gaps: {run.Tenants} tenants, {run.Waiting} waiting command(s), {run.Overdue.Count} tablet(s) over 24 h, {run.Failed} failed");
    return run.Failed == 0 && run.Overdue.Count == 0 ? 0 : 1;
}

// `Karry.Workers generate-shifts`: one run of the shift generator, then exit. Exit 1 if a tenant failed.
if (args is ["generate-shifts", ..])
{
    using var once = builder.Build();
    var run = await once.Services.GetRequiredService<GenerateShiftInstances>().RunAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"shifts: {run.Tenants} tenants, {run.Sites} sites, {run.Added} added, {run.Changed} changed, {run.Removed} removed, {run.Failed} failed");
    return run.Failed == 0 ? 0 : 1;
}

// `Karry.Workers delete-import-files`: one run of the 90-day retention of import files, then exit. Exit 1 if a tenant failed.
if (args is ["delete-import-files", ..])
{
    using var once = builder.Build();
    var run = await once.Services.GetRequiredService<DeleteExpiredImportFiles>().RunAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"import files: {run.Tenants} tenants, {run.Deleted} deleted, {run.Failed} failed");
    return run.Failed == 0 ? 0 : 1;
}

// `Karry.Workers import-sign-ins`: one import of Keycloak's failed sign-ins into the ledgers, then exit (smoke test).
if (args is ["import-sign-ins", ..])
{
    if (!WorkerServices.HasKeycloak(builder.Configuration))
    {
        await Console.Error.WriteLineAsync("Identity:Keycloak:BaseUrl is not configured.").ConfigureAwait(false);
        return 2;
    }

    using var once = builder.Build();
    var run = await once.Services.GetRequiredService<ImportSignInFailures>().RunAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"sign-in failures: {run.Imported} imported, {run.AlreadyImported} already imported, {run.Unattributed} not attributable to a tenant");
    return 0;
}

builder.Services.AddWorkerJobs(builder.Configuration);
using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return 0;

// Every eligible message once (a failure is rescheduled, so it is not eligible again in this run), then what is left.
static async Task<int> DrainAsync<TContext>(OutboxDispatcher<TContext> dispatcher)
    where TContext : Karry.BuildingBlocks.Persistence.ModuleDbContext
{
    await dispatcher.DispatchPendingAsync(int.MaxValue, CancellationToken.None).ConfigureAwait(false);
    return await dispatcher.PendingAsync(CancellationToken.None).ConfigureAwait(false);
}
