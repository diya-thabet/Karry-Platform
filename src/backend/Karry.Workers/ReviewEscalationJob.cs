using Karry.Modules.Verification.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// The SLA of reviews (US-P2-VER-06 AC1, ADR-0037 §6): every 5 minutes, items waiting past their due time are escalated
/// once, ledgered. One run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class ReviewEscalationJob(EscalateOverdueReviews escalate) : IJob
{
    public const string Schedule = "0 */5 * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await escalate.RunAsync(cancellationToken).ConfigureAwait(false);
}
