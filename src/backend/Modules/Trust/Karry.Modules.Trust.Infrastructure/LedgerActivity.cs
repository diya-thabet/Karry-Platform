using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>
/// Trust's column of the operator console (ADR-0029 §3): the company's last activity, the time of its newest ledger
/// entry (every business change is ledgered; reads are not, so a company that only reads looks idle).
/// </summary>
internal sealed class LedgerActivity(TrustDbContext db) : ITenantSummarySource
{
    public async Task<TenantSummaryPart> SummarizeAsync(TenantSummaryRequest request, CancellationToken cancellationToken) =>
        new(LastActivity: await db.LedgerEntries.AsNoTracking().MaxAsync(e => (DateTimeOffset?)e.OccurredAt, cancellationToken).ConfigureAwait(false));
}
