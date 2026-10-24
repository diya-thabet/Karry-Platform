using Karry.BuildingBlocks.Observability;
using Karry.BuildingBlocks.Outbox;
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
    Console.WriteLine($"outbox: {left} message(s) left for a retry");
    return left == 0 ? 0 : 1;
}

// Subscribers of each module's events (ADR-0015): Tenancy's tenant_provisioned sets up the tenant in Keycloak.
builder.Services.AddOutboxDispatcherService<TenancyDbContext>();
builder.Services.AddOutboxDispatcherService<IdentityDbContext>();

if (anchoring)
{
    builder.Services.AddQuartz(quartz => quartz.ScheduleJob<LedgerAnchoringJob>(trigger => trigger
        .WithIdentity("ledger-anchoring")
        .StartNow()
        .WithCronSchedule(LedgerAnchoringJob.Schedule)));
    builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
}

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
