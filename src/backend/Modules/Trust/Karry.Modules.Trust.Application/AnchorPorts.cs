using Karry.Modules.Trust.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Trust.Application;

/// <summary>Signs anchors (software key locally; KMS in the environments, P1.17).</summary>
public interface IAnchorSigner
{
    string KeyId { get; }

    byte[] Sign(byte[] data);
}

/// <summary>Verifies anchor signatures with the trusted public keys, by key id.</summary>
public interface IAnchorVerifier
{
    bool Verify(string keyId, byte[] data, byte[] signature);
}

/// <summary>WORM object storage for anchors (S3 Object Lock, compliance mode).</summary>
public interface IAnchorStore
{
    /// <summary>Writes the document under <see cref="LedgerAnchor.ObjectKey"/> with compliance retention.</summary>
    Task PutAsync(LedgerAnchor anchor, string document, CancellationToken cancellationToken);

    /// <summary>
    /// All anchors of a tenant in sequence order, each read from its <b>oldest</b> version: Object Lock stops deleting or
    /// altering a version, not adding a newer one, so a later version is a forgery attempt and is reported via
    /// <see cref="StoredAnchor.Versions"/>.
    /// </summary>
    IAsyncEnumerable<StoredAnchor> ListAsync(TenantId tenant, CancellationToken cancellationToken);
}

/// <summary>An anchor object as stored: its original document and how many versions exist under its key.</summary>
public sealed record StoredAnchor(string Document, int Versions);

/// <summary>
/// One anchoring run at a time across every Workers process (hardening plan H-C5): two runs reading the same head
/// would both write its anchor, and the second write is a newer version in the WORM bucket, which the verifier must
/// (and does) report as tampering. Released when disposed, or when the process dies.
/// </summary>
public interface IAnchoringLock
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}

/// <summary>The current tenant's chain head and anchoring bookkeeping (row-level security scopes it).</summary>
public interface IAnchorBookkeeping
{
    Task<(long Sequence, string Hash)?> HeadAsync(CancellationToken cancellationToken);

    Task<long> LastAnchoredSequenceAsync(CancellationToken cancellationToken);

    Task RecordAsync(LedgerAnchor anchor, CancellationToken cancellationToken);
}
