using Karry.Modules.FieldOps.Application;
using Quartz;

namespace Karry.Workers;

/// <summary>
/// RULE-FO-08 "missing end" (US-P2-VER-08 AC2, ADR-0038 §5): every 5 minutes, segments still open the rule's grace after
/// their shift ended are flagged, company by company. One run at a time.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class MissingEndJob(RaiseMissingEnds raise) : IJob
{
    public const string Schedule = "0 2/5 * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await raise.RunAsync(cancellationToken).ConfigureAwait(false);
}
