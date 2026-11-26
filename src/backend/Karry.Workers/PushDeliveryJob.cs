using Karry.Modules.Notifications.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Web Push deliveries (ADR-0039 §5): every minute, the waiting pushes are sent company by company, a failure retried
/// with backoff and a subscription that is gone deleted. One run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class PushDeliveryJob(DeliverPushes deliver) : IJob
{
    public const string Schedule = "30 * * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await deliver.RunAsync(cancellationToken).ConfigureAwait(false);
}
