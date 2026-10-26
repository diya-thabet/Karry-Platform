using Karry.Modules.Organization.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Daily shift instances (US-P1-ORG-02 AC3, ADR-0024): every site's stored instances of the next 30 days follow its
/// pattern versions, non-working days and clock. Idempotent; one run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class ShiftInstanceJob(GenerateShiftInstances generate) : IJob
{
    public const string Schedule = "0 15 0 * * ?"; // daily at 00:15 UTC, plus once at start-up (catch-up after downtime; Program.Schedule)

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await generate.RunAsync(cancellationToken).ConfigureAwait(false);
}
