namespace Karry.Modules.Trust.Contracts;

/// <summary>A server signature (ES256, IEEE P1363, base64) and the key version that made it.</summary>
public sealed record DigestSignature(string KeyId, string Signature);

/// <summary>
/// The server's signing key for other modules (ADR-0033 §3, ADR-0040 §3): signs a SHA-256 digest. The service may be
/// absent (local, no OpenBao) or down: then the answer is null and the caller tries again later, never signing another way.
/// </summary>
public interface IDigestSigner
{
    Task<DigestSignature?> TrySignAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken);
}
