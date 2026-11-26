using Karry.Modules.Notifications.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Daily deletion of notices older than 90 days (ADR-0039 §1): notifications, e-mail deliveries and pushes, company by
/// company. Idempotent; one run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class NoticeRetentionJob(PurgeNotices purge) : IJob
{
    public const string Schedule = "0 15 2 * * ?"; // daily at 02:15 UTC, plus once at start-up (catch-up after downtime)

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await purge.RunAsync(cancellationToken).ConfigureAwait(false);
}
