using Karry.Modules.Billing.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Hourly run of the subscription time rules (trial end, unpaid days, retention before deletion; US-P1-BIL-02 AC2).
/// Hourly rather than daily so a rule applies within the hour it falls due; idempotent, one run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class SubscriptionLifecycleJob(AdvanceSubscriptions lifecycle) : IJob
{
    public const string Schedule = "0 20 * * * ?"; // every hour at :20, plus once at start-up (catch-up after downtime)

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await lifecycle.RunAsync(cancellationToken).ConfigureAwait(false);
}
