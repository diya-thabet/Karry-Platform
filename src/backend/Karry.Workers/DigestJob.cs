using Karry.Modules.Notifications.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// The daily e-mail digest (ADR-0039 §4, TC-P2-NOT-003): every 15 minutes, each site whose local time has reached 07:00
/// (until noon, to catch up after downtime) gets its digests queued, once per person, site and day; the e-mail job sends
/// them. One run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class DigestJob(QueueDigests digests) : IJob
{
    public const string Schedule = "0 1/15 * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await digests.RunAsync(cancellationToken).ConfigureAwait(false);
}
