using System.Net;
using System.Security.Cryptography;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Infrastructure;
using Karry.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Trust.Tests;

/// <summary>OpenBao for this class's tests (<see cref="OpenBaoServer"/>).</summary>
public sealed class OpenBao : IAsyncLifetime
{
    private OpenBaoServer? server;

    public OpenBaoServer Server => server!;

    public async ValueTask InitializeAsync() => server = await OpenBaoServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }
}

/// <summary>The server's signing key in OpenBao transit (ADR-0033 §3, US-P2-SIG-03).</summary>
[Trait("Story", "US-P2-SIG-03")]
public sealed class OpenBaoTransitSignerTests(OpenBao bao) : IClassFixture<OpenBao>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Digest = SHA256.HashData("envelope hash and receivedAt"u8);

    [Fact]
    public async Task A_digest_is_signed_in_p1363_and_verifies_with_the_published_key_of_its_version()
    {
        using var signer = Signer($"k-{Guid.NewGuid():N}");

        var signature = await signer.SignAsync(Digest, Ct);
        var keys = await signer.KeysAsync(Ct);

        signature.Signature.Should().HaveCount(64);
        keys.Should().ContainSingle().Which.KeyId.Should().Be(signature.KeyId);
        ServerSignatures.Verify(keys[0].PublicKeyPem, Digest, signature.Signature).Should().BeTrue();
        ServerSignatures.Verify(keys[0].PublicKeyPem, SHA256.HashData("another"u8), signature.Signature).Should().BeFalse();
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-004")]
    public async Task After_a_rotation_the_new_version_signs_and_signatures_of_the_old_one_still_verify()
    {
        var name = $"k-{Guid.NewGuid():N}";
        using var signer = Signer(name);
        var before = await signer.SignAsync(Digest, Ct);

        await bao.Server.ExecAsync("bao", "write", "-f", $"transit/keys/{name}/rotate");
        var after = await signer.SignAsync(Digest, Ct);
        var keys = (await signer.KeysAsync(Ct)).ToDictionary(k => k.KeyId, k => k.PublicKeyPem);

        (before.KeyId, after.KeyId).Should().Be(($"{name}-v1", $"{name}-v2"));
        ServerSignatures.Verify(keys[before.KeyId], Digest, before.Signature).Should().BeTrue();
        ServerSignatures.Verify(keys[after.KeyId], Digest, after.Signature).Should().BeTrue();
        ServerSignatures.Verify(keys[after.KeyId], Digest, before.Signature).Should().BeFalse("a verifier picks the key by kid");
    }

    [Fact]
    public async Task A_signing_service_that_does_not_answer_is_reported_unavailable_and_a_refusal_as_a_configuration_error()
    {
        // Nothing listens on port 1: the connection is refused.
        using var down = new OpenBaoTransitSigner(new ServerSigningOptions { Address = "http://127.0.0.1:1", Token = "t", Timeout = TimeSpan.FromSeconds(3) });
        using var badToken = new OpenBaoTransitSigner(new ServerSigningOptions { Address = bao.Server.Address, Token = "not-a-token" });
        using var sealedLike = new OpenBaoTransitSigner(new ServerSigningOptions { Address = "http://bao", Token = "t" }, new Answer(HttpStatusCode.ServiceUnavailable));

        await FluentActions.Awaiting(() => down.SignAsync(Digest, Ct)).Should().ThrowAsync<ServerSignerUnavailableException>();
        await FluentActions.Awaiting(() => sealedLike.SignAsync(Digest, Ct)).Should().ThrowAsync<ServerSignerUnavailableException>();
        await FluentActions.Awaiting(() => badToken.SignAsync(Digest, Ct)).Should().ThrowExactlyAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Without_an_address_nothing_is_signed_and_an_address_without_a_token_does_not_start()
    {
        static IServiceCollection Services(Dictionary<string, string?> settings) =>
            new ServiceCollection().AddTrustServerSigning(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        using var none = Services([]).BuildServiceProvider();
        var signer = none.GetRequiredService<IServerSigner>();

        signer.Available.Should().BeFalse();
        await FluentActions.Awaiting(() => signer.SignAsync(Digest, Ct)).Should().ThrowAsync<ServerSignerUnavailableException>();
        FluentActions.Invoking(() => Services(new() { ["Trust:ServerSigning:Address"] = "http://bao" })).Should().Throw<InvalidOperationException>();
    }

    private OpenBaoTransitSigner Signer(string keyName) => new(new ServerSigningOptions
    {
        Address = bao.Server.Address,
        Token = OpenBaoServer.RootToken,
        KeyName = keyName,
        CreateKeyIfMissing = true,
    });

    private sealed class Answer(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
