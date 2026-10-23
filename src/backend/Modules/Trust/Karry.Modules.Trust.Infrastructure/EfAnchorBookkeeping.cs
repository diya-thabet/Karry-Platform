using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

internal sealed class EfAnchorBookkeeping(TrustDbContext db) : IAnchorBookkeeping
{
    public async Task<(long Sequence, string Hash)?> HeadAsync(CancellationToken cancellationToken)
    {
        var head = await db.LedgerHeads.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return head is null || head.LastSeq == 0 ? null : (head.LastSeq, head.LastHash);
    }

    public async Task<long> LastAnchoredSequenceAsync(CancellationToken cancellationToken) =>
        await db.Anchors.AsNoTracking().MaxAsync(a => (long?)a.Seq, cancellationToken).ConfigureAwait(false) ?? 0;

    public async Task RecordAsync(LedgerAnchor anchor, CancellationToken cancellationToken)
    {
        db.Anchors.Add(new AnchorRow
        {
            TenantId = anchor.Tenant,
            Seq = anchor.Sequence,
            EntryHash = anchor.EntryHash,
            AnchoredAt = anchor.AnchoredAt,
            ObjectKey = anchor.ObjectKey,
            KeyId = anchor.KeyId,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
