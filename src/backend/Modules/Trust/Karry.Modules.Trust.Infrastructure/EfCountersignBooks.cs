using System.Buffers.Text;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Trust.Infrastructure;

internal sealed class EfCountersignBook(TrustDbContext db, ITenantContext tenant) : ICountersignBook
{
    public async Task<IReadOnlyList<PendingEnvelope>> PendingAsync(int limit, CancellationToken cancellationToken) =>
        await db.SignatureEnvelopes.AsNoTracking()
            .Where(e => !db.Countersignatures.Any(c => c.EnvelopeId == e.EnvelopeId))
            .OrderBy(e => e.ReceivedAt).ThenBy(e => e.EnvelopeId)
            .Take(limit)
            .Select(e => new PendingEnvelope(e.EnvelopeId, e.EnvelopeHash, e.ReceivedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> RecordAsync(PendingEnvelope envelope, ServerSignature signature, DateTimeOffset signedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(signature);
        var row = new EnvelopeCountersignatureRow
        {
            EnvelopeId = envelope.EnvelopeId,
            TenantId = tenant.Current ?? throw new InvalidOperationException("Countersigning runs for a tenant."),
            KeyId = signature.KeyId,
            Signature = Base64Url.EncodeToString(signature.Signature),
            SignedAt = signedAt,
        };
        db.Countersignatures.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another run countersigned it first; its countersignature stands.
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
    }
}

internal sealed class EfServerKeyBook(TrustDbContext db) : IServerKeyBook
{
    public async Task<IReadOnlySet<string>> KeyIdsAsync(CancellationToken cancellationToken) =>
        (await db.ServerKeys.AsNoTracking().Select(k => k.KeyId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

    public async Task AddAsync(IReadOnlyList<ServerKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            // Never changes a published key: an existing kid keeps the key it was published with.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO audit.server_keys (key_id, public_key, created_at) VALUES ({key.KeyId}, {key.PublicKeyPem}, {key.CreatedAt}) ON CONFLICT (key_id) DO NOTHING",
                cancellationToken).ConfigureAwait(false);
        }
    }
}
