using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Contracts;
using Karry.Modules.Trust.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Trust.Tests;

/// <summary>
/// The server signer for other modules (ADR-0040 §3): a signature and its key version while the signing service
/// answers; null, never an exception, while it is absent or down, so the caller tries again later.
/// </summary>
[Trait("Story", "US-P2-SIG-05")]
public sealed class DigestSignerTests
{
    private static readonly byte[] Digest = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    [Fact]
    public async Task Without_a_signing_service_the_answer_is_null()
    {
        await using var services = Services(null);

        (await services.GetRequiredService<IDigestSigner>().TrySignAsync(Digest, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task With_the_service_the_signature_and_its_key_version_come_back_and_a_down_service_is_null()
    {
        var signer = new Signer();
        await using var services = Services(signer);
        var digests = services.GetRequiredService<IDigestSigner>();

        (await digests.TrySignAsync(Digest, CancellationToken.None)).Should().Be(new DigestSignature("karry-server-v2", Convert.ToBase64String(new byte[64])));
        signer.Signed.Should().ContainSingle().Which.Should().Equal(Digest);
        signer.Down = true;
        (await digests.TrySignAsync(Digest, CancellationToken.None)).Should().BeNull();
    }

    private static ServiceProvider Services(IServerSigner? signer)
    {
        var services = new ServiceCollection().AddLogging();
        if (signer is not null)
        {
            services.AddSingleton(signer);
        }

        return services.AddTrustServerSigning(new ConfigurationBuilder().Build()).BuildServiceProvider();
    }

    private sealed class Signer : IServerSigner
    {
        public bool Down { get; set; }

        public List<byte[]> Signed { get; } = [];

        public bool Available => true;

        public Task<ServerSignature> SignAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new ServerSignerUnavailableException("sealed");
            }

            Signed.Add(sha256Digest.ToArray());
            return Task.FromResult(new ServerSignature("karry-server-v2", new byte[64]));
        }

        public Task<IReadOnlyList<ServerKey>> KeysAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ServerKey>>([]);
    }
}
