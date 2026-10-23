using System.Runtime.CompilerServices;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

internal sealed class EfLedgerReader(TrustDbContext db) : ILedgerReader
{
    public async IAsyncEnumerable<LedgerEntry> ReadChainAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Streamed, not buffered: a tenant's chain grows without bound.
        await foreach (var row in db.LedgerEntries.AsNoTracking().OrderBy(e => e.Seq).AsAsyncEnumerable()
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new LedgerEntry(row.TenantId, row.Seq, row.EventId, row.EventType, row.OccurredAt, row.Payload, row.PayloadHash, row.PrevHash, row.EntryHash);
        }
    }
}
