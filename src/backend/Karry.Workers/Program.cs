using Karry.BuildingBlocks.Observability;
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
