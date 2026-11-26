using Karry.Modules.Documents.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// Signs the issued documents' envelopes with the server key (ADR-0040 §3) every minute, when the signing service
/// answers; until then they wait, valid from their ledger entry. One run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class DocumentSigningJob(SignDocuments sign) : IJob
{
    public const string Schedule = "20 * * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await sign.RunAsync(cancellationToken).ConfigureAwait(false);
}
