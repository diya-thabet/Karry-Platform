using Karry.Modules.Integrity.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Continuity gaps left unsettled beyond 48 h (US-P2-FO-08 AC2, ADR-0037 §7): every 15 minutes, RULE-FO-11
/// <c>FRAUD_SUSPECTED</c> is raised once per gap, ledgered. One run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class UnsettledGapsJob(RaiseUnsettledGaps raise) : IJob
{
    public const string Schedule = "0 */15 * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await raise.RunAsync(cancellationToken).ConfigureAwait(false);
}
