using System.Security.Cryptography;

namespace Karry.Modules.Trust.Application;

/// <summary>
/// The server's signing key, behind a port (ADR-0033 §3): the private key never enters the API's memory or settings.
/// OpenBao transit locally, in CI and on-premises; a cloud KMS adapter later (P1.17). Signatures are ES256 over a
/// SHA-256 digest, in IEEE P1363 form (64 bytes), as the device signatures.
/// </summary>
public interface IServerSigner
{
    /// <summary>False when no signing service is configured: countersigning then waits (ADR-0033 §4).</summary>
    bool Available { get; }

    /// <summary>
    /// Signs a SHA-256 digest with the current key version. Throws <see cref="ServerSignerUnavailableException"/> when
    /// the service does not answer, so the caller retries later.
    /// </summary>
    Task<ServerSignature> SignAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken);

    /// <summary>Every key version, old ones included: they stay verify-only forever (ADR-0033 §3, rotation by kid).</summary>
    Task<IReadOnlyList<ServerKey>> KeysAsync(CancellationToken cancellationToken);
}

/// <summary>A signature and the key version that made it (<c>kid</c>, e.g. <c>karry-server-v3</c>).</summary>
public sealed record ServerSignature(string KeyId, byte[] Signature);

/// <summary>One key version: its <c>kid</c>, public key (SPKI PEM) and when it was created.</summary>
public sealed record ServerKey(string KeyId, string PublicKeyPem, DateTimeOffset CreatedAt);

/// <summary>The signing service did not answer (down, sealed, timeout): retry later; never sign another way.</summary>
public sealed class ServerSignerUnavailableException : Exception
{
    public ServerSignerUnavailableException()
    {
    }

    public ServerSignerUnavailableException(string message)
        : base(message)
    {
    }

    public ServerSignerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Checks a server signature with a published public key; needs no access to the signing service.</summary>
public static class ServerSignatures
{
    public static bool Verify(string publicKeyPem, ReadOnlySpan<byte> sha256Digest, ReadOnlySpan<byte> signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        if (sha256Digest.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key.KeySize == 256 && key.VerifyHash(sha256Digest, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}

/// <summary>No signing service configured: nothing is signed, and the countersign job leaves envelopes pending.</summary>
public sealed class UnavailableServerSigner : IServerSigner
{
    public bool Available => false;

    public Task<ServerSignature> SignAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken) =>
        throw new ServerSignerUnavailableException("No server signing service is configured (Trust:ServerSigning).");

    public Task<IReadOnlyList<ServerKey>> KeysAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ServerKey>>([]);
}
