using Karry.Modules.Trust.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Countersigns stored signature envelopes (ADR-0033 §4): every 30 seconds, so the target of 5 minutes holds with room
/// for a slow signing service (alert <c>KarryCountersignLagHigh</c> at 15). One run at a time in a process; a second
/// process only re-signs what the first has not stored yet, and the first countersignature stored stands.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class CountersignJob(CountersignEnvelopes countersign) : IJob
{
    public const string Schedule = "0/30 * * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await countersign.RunAsync(cancellationToken).ConfigureAwait(false);
}
