using Karry.Modules.Imports.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Daily deletion of import files older than 90 days (US-P1-ORG-03 AC5, ADR-0027 §9), each recorded in the company's
/// ledger. Idempotent; one run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class ImportRetentionJob(DeleteExpiredImportFiles delete) : IJob
{
    public const string Schedule = "0 45 1 * * ?"; // daily at 01:45 UTC, plus once at start-up (catch-up after downtime; Program.Schedule)

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await delete.RunAsync(cancellationToken).ConfigureAwait(false);
}
