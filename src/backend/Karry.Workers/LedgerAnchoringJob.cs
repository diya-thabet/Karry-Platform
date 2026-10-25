using Karry.Modules.Trust.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>Hourly WORM anchoring of every tenant's chain head (US-P1-AUD-02). One run at a time per worker.</summary>
[DisallowConcurrentExecution]
internal sealed class LedgerAnchoringJob(AnchorLedgers anchoring) : IJob
{
    public const string Schedule = "0 5 * * * ?"; // every hour at :05, plus once at start-up (catch-up after downtime; Program.Schedule)

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await anchoring.RunAsync(cancellationToken).ConfigureAwait(false);
}
