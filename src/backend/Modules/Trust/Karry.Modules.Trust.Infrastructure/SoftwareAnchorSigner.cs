using System.Security.Cryptography;
using Karry.Modules.Trust.Application;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>
/// ECDSA P-256 / SHA-256 anchor signatures in IEEE P1363 form (as the rest of the trust layer, ADR-0007). The key id is
/// the first 16 hex characters of SHA-256 over the public key (SPKI), so a verifier can pick the right trusted key.
/// </summary>
public sealed class SoftwareAnchorSigner : IAnchorSigner, IAnchorVerifier, IDisposable
{
    private readonly ECDsa key;
    private readonly Dictionary<string, ECDsa> trusted = new(StringComparer.Ordinal);

    public SoftwareAnchorSigner(ECDsa key, IEnumerable<string>? trustedPublicKeysPem = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.KeySize != 256)
        {
            throw new ArgumentException("Anchor signing key must be ECDSA P-256.", nameof(key));
        }

        this.key = key;
        KeyId = IdOf(key);
        trusted[KeyId] = key;
        foreach (var pem in trustedPublicKeysPem ?? [])
        {
            var publicKey = ECDsa.Create();
            publicKey.ImportFromPem(pem);
            trusted[IdOf(publicKey)] = publicKey;
        }
    }

    public string KeyId { get; }

    public byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public bool Verify(string keyId, byte[] data, byte[] signature) =>
        trusted.TryGetValue(keyId, out var publicKey)
        && publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static string IdOf(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];
    }

    public void Dispose()
    {
        foreach (var publicKey in trusted.Values.Distinct())
        {
            publicKey.Dispose();
        }
    }
}
