using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Contracts;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>The contract over the server signer: null when it is not configured or does not answer.</summary>
internal sealed class ServerDigestSigner(IServerSigner signer) : IDigestSigner
{
    public async Task<DigestSignature?> TrySignAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken)
    {
        if (!signer.Available)
        {
            return null;
        }

        try
        {
            var signature = await signer.SignAsync(sha256Digest, cancellationToken).ConfigureAwait(false);
            return new DigestSignature(signature.KeyId, Convert.ToBase64String(signature.Signature));
        }
        catch (ServerSignerUnavailableException)
        {
            return null;
        }
    }
}
