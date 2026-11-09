using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Karry.Modules.Trust.Contracts;
using Karry.Testing;

namespace Karry.Modules.Trust.Tests;

/// <summary>
/// Signature envelope v1 on the server (ADR-0033 §1): the same vectors as <c>@karry/crypto</c>, from an independent
/// implementation, so an envelope signed in the browser verifies in .NET (P2.3 card, cross-language test).
/// </summary>
[Trait("Story", "US-P2-SIG-02")]
public sealed class SignatureEnvelopesTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "tests", "vectors", "signature-envelope.json"))).RootElement;

    private static readonly byte[] X = Base64Url.DecodeFromChars(Vectors.GetProperty("devicePublicKey").GetProperty("x").GetString());
    private static readonly byte[] Y = Base64Url.DecodeFromChars(Vectors.GetProperty("devicePublicKey").GetProperty("y").GetString());

    public static TheoryData<string> Valid => [.. Vectors.GetProperty("valid").EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

    public static TheoryData<string> Invalid => [.. Vectors.GetProperty("invalid").EnumerateArray().Select(v => v.GetProperty("name").GetString()!)];

    [Fact]
    public void The_payload_hash_is_that_of_the_independent_implementation() =>
        SignatureEnvelopes.PayloadHash(Vectors.GetProperty("payload")).Should().Be(Vectors.GetProperty("payloadHash").GetString());

    [Theory]
    [MemberData(nameof(Valid))]
    public void A_valid_envelope_gives_the_same_text_and_hash_and_its_device_signature_verifies(string name)
    {
        var vector = Case("valid", name);
        var envelope = vector.GetProperty("envelope");

        SignatureEnvelopes.Problem(envelope).Should().BeNull();
        Encoding.UTF8.GetString(SignatureEnvelopes.Canonical(envelope)).Should().Be(vector.GetProperty("canonical").GetString());
        SignatureEnvelopes.Hash(envelope).Should().Be(vector.GetProperty("envelopeHash").GetString());
        SignatureEnvelopes.VerifyDevice(envelope, X, Y, vector.GetProperty("deviceSignature").GetString()!).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Valid))]
    [Trait("TestCase", "TC-P2-SIG-002")]
    public void The_signature_does_not_verify_once_one_character_changed_or_with_another_key(string name)
    {
        var vector = Case("valid", name);
        var signature = vector.GetProperty("deviceSignature").GetString()!;
        using var tampered = JsonDocument.Parse(vector.GetProperty("tamperedCanonical").GetString()!);
        using var otherTablet = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var other = otherTablet.ExportParameters(false).Q;

        SignatureEnvelopes.VerifyDevice(tampered.RootElement, X, Y, signature).Should().BeFalse();
        SignatureEnvelopes.VerifyDevice(vector.GetProperty("envelope"), other.X, other.Y, signature).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void A_malformed_envelope_is_refused_and_never_verifies(string name)
    {
        var envelope = Case("invalid", name).GetProperty("envelope");

        SignatureEnvelopes.Problem(envelope).Should().NotBeNull();
        SignatureEnvelopes.VerifyDevice(envelope, X, Y, Case("valid", "minimal").GetProperty("deviceSignature").GetString()!).Should().BeFalse();
    }

    private static JsonElement Case(string kind, string name) =>
        Vectors.GetProperty(kind).EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);
}
