using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

internal sealed class EfEnvelopeEvidence(TrustDbContext db) : IEnvelopeEvidence
{
    public async Task<EnvelopeEvidence?> FindAsync(Guid envelopeId, CancellationToken cancellationToken)
    {
        var row = await db.SignatureEnvelopes.AsNoTracking().SingleOrDefaultAsync(e => e.EnvelopeId == envelopeId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var countersignature = await (
                from c in db.Countersignatures.AsNoTracking()
                where c.EnvelopeId == envelopeId
                join k in db.ServerKeys.AsNoTracking() on c.KeyId equals k.KeyId into keys
                from k in keys.DefaultIfEmpty()
                select new StoredCountersignature(c.KeyId, c.Signature, c.SignedAt, k == null ? null : k.PublicKey))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var entry = row.LedgerEventId is { } eventId
            ? await db.LedgerEntries.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId, cancellationToken).ConfigureAwait(false)
            : null;
        var previous = entry is { Seq: > 1 }
            ? await db.LedgerEntries.AsNoTracking().Where(e => e.TenantId == entry.TenantId && e.Seq == entry.Seq - 1).Select(e => e.EntryHash)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return new EnvelopeEvidence(
            new StoredEnvelope(row.EnvelopeId, row.RecordType, row.RecordId, row.RecordVersion, row.EnvelopeHash, row.Canonical, row.DeviceId, row.DeviceSignature, row.ReceivedAt),
            countersignature,
            entry is null ? null : new LedgerEntry(entry.TenantId, entry.Seq, entry.EventId, entry.EventType, entry.OccurredAt, entry.Payload, entry.PayloadHash, entry.PrevHash, entry.EntryHash),
            previous);
    }
}
