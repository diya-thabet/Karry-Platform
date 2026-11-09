using Karry.Modules.Sync.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Sequence gaps of tablets (ADR-0031 §4): every 5 minutes, how many commands wait for an earlier number and for how
/// long (alert <c>KarrySyncGapOpen</c> after 24 hours). Read-only; one run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class SyncGapJob(MeasureSyncGaps measure) : IJob
{
    public const string Schedule = "0 */5 * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await measure.RunAsync(cancellationToken).ConfigureAwait(false);
}
