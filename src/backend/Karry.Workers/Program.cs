using Karry.BuildingBlocks.Observability;
using Karry.BuildingBlocks.Outbox;
using Karry.Modules.Billing.Application;
using Karry.Modules.Billing.Infrastructure;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Infrastructure;
using Karry.Modules.Tenancy.Infrastructure;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Infrastructure;
using Karry.Workers;
using Quartz;

var builder = Host.CreateApplicationBuilder(args);
builder.AddKarryObservability("karry-workers");

var connectionString = builder.Configuration.GetConnectionString("Karry")
    ?? throw new InvalidOperationException("ConnectionStrings:Karry is required.");
builder.Services.AddTenancyInfrastructure(connectionString);
builder.Services.AddTrustInfrastructure(connectionString);
builder.Services.AddTrustAnchoring(builder.Configuration);
builder.Services.AddIdentityInfrastructure(builder.Configuration, connectionString);

// Billing subscribes to tenant provisioning (the trial subscription) and runs the subscription lifecycle.
builder.Services.AddBillingInfrastructure(connectionString);

var anchoring = builder.Services.Any(s => s.ServiceType == typeof(AnchorLedgers));

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

// `Karry.Workers import-sign-ins`: one import of Keycloak's failed sign-ins into the ledgers, then exit (smoke test).
var keycloak = !string.IsNullOrWhiteSpace(builder.Configuration["Identity:Keycloak:BaseUrl"]);
if (args is ["import-sign-ins", ..])
{
    if (!keycloak)
    {
        await Console.Error.WriteLineAsync("Identity:Keycloak:BaseUrl is not configured.").ConfigureAwait(false);
        return 2;
    }

    using var once = builder.Build();
    var run = await once.Services.GetRequiredService<ImportSignInFailures>().RunAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"sign-in failures: {run.Imported} imported, {run.AlreadyImported} already imported, {run.Unattributed} not attributable to a tenant");
    return 0;
}

// Subscribers of each module's events (ADR-0015): Tenancy's tenant_provisioned sets up the tenant in Keycloak and
// starts its trial subscription in Billing.
builder.Services.AddOutboxDispatcherService<TenancyDbContext>();
builder.Services.AddOutboxDispatcherService<IdentityDbContext>();
builder.Services.AddOutboxDispatcherService<BillingDbContext>();

builder.Services.AddQuartz(quartz =>
{
    quartz.ScheduleJob<SubscriptionLifecycleJob>(trigger => trigger
        .WithIdentity("subscription-lifecycle")
        .StartNow()
        .WithCronSchedule(SubscriptionLifecycleJob.Schedule));

    if (anchoring)
    {
        quartz.ScheduleJob<LedgerAnchoringJob>(trigger => trigger
            .WithIdentity("ledger-anchoring")
            .StartNow()
            .WithCronSchedule(LedgerAnchoringJob.Schedule));
    }

    if (keycloak)
    {
        quartz.ScheduleJob<SignInFailureImportJob>(trigger => trigger
            .WithIdentity("sign-in-failure-import")
            .StartNow()
            .WithCronSchedule(SignInFailureImportJob.Schedule));
    }
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

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
