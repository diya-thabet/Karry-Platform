using Karry.Modules.Notifications.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// E-mail deliveries (ADR-0039 §4): every minute, the due e-mails are sent company by company, a failure retried with
/// backoff. One run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class EmailDeliveryJob(DeliverEmails deliver) : IJob
{
    public const string Schedule = "0 * * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await deliver.RunAsync(cancellationToken).ConfigureAwait(false);
}
