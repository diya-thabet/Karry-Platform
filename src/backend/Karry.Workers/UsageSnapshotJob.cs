using Karry.Modules.Billing.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Daily usage snapshot (US-P1-BIL-04 AC1): every tenant's metrics with the limit that applied. Re-running the same day
/// replaces that day's values; one run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class UsageSnapshotJob(SnapshotUsage snapshot) : IJob
{
    public const string Schedule = "0 30 2 * * ?"; // daily at 02:30 UTC, plus once at start-up (catch-up after downtime; Program.Schedule)

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await snapshot.RunAsync(cancellationToken).ConfigureAwait(false);
}
