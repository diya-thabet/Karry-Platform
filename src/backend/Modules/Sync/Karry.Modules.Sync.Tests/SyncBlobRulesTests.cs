using System.Security.Cryptography;
using System.Text.Json;
using Karry.BuildingBlocks.Storage;
using Karry.Modules.Sync.Application;

namespace Karry.Modules.Sync.Tests;

/// <summary>What a tablet may ask to upload (ADR-0032 §2), and the key built for it (§1).</summary>
public sealed class SyncBlobRulesTests
{
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("../../tenants/x")]
    public void Only_64_lowercase_hex_characters_are_a_hash(string? value) => SyncBlobRules.IsSha256Hex(value).Should().BeFalse();

    [Fact]
    public void A_hash_is_lowercase_hex_and_its_checksum_header_is_base64_of_the_same_bytes()
    {
        SyncBlobRules.IsSha256Hex(Hash).Should().BeTrue();
        SyncBlobRules.IsSha256Hex(Hash.ToUpperInvariant()).Should().BeFalse();
        SyncBlobRules.ToBase64(Hash).Should().Be(Convert.ToBase64String(SHA256.HashData([1, 2, 3])));
    }

    [Theory]
    [InlineData(1, "image/jpeg", true)]
    [InlineData(SyncBlobRules.MaxBytes, "application/json", true)]
    [InlineData(SyncBlobRules.MaxBytes + 1, "image/png", false)]
    [InlineData(0, "image/webp", false)]
    [InlineData(10, "image/gif", false)]
    [InlineData(10, "IMAGE/JPEG", false)]
    public void A_blob_is_at_most_1_MB_of_a_known_type(long size, string contentType, bool valid) =>
        new PresignBlobValidator().Validate(new PresignBlob(Hash, size, contentType)).IsValid.Should().Be(valid);

    [Fact]
    public void A_payload_without_evidence_names_no_blob() =>
        CommandEvidenceRefs.Read(Json("""{"n":1}""")).Should().BeEmpty();

    [Fact]
    public void A_payload_s_evidence_lists_each_blob_once()
    {
        var refs = CommandEvidenceRefs.Read(Json($$"""
            {"evidence":[{"sha256":"{{Hash}}","size":10,"contentType":"image/jpeg","capturedAt":"2026-11-09T08:00:00Z"},
                         {"sha256":"{{Hash}}","size":10,"contentType":"image/jpeg"}]}
            """));

        refs.Should().Equal(new EvidenceRef(Hash, 10, "image/jpeg"));
    }

    [Theory]
    [InlineData("""{"evidence":{}}""")]
    [InlineData("""{"evidence":[1]}""")]
    [InlineData("""{"evidence":[{"sha256":"abc","size":10,"contentType":"image/jpeg"}]}""")]
    [InlineData("""{"evidence":[{"sha256":"HASH","size":0,"contentType":"image/jpeg"}]}""")]
    [InlineData("""{"evidence":[{"sha256":"HASH","size":"10","contentType":"image/jpeg"}]}""")]
    [InlineData("""{"evidence":[{"sha256":"HASH","size":1048577,"contentType":"image/jpeg"}]}""")]
    [InlineData("""{"evidence":[{"sha256":"HASH","size":10,"contentType":"image/gif"}]}""")]
    [InlineData("""{"evidence":[{"sha256":"HASH","size":10}]}""")]
    [InlineData("""{"evidence":[{"sha256":"HASH","size":10,"contentType":"image/jpeg"},{"sha256":"HASH","size":11,"contentType":"image/jpeg"}]}""")]
    public void Malformed_evidence_is_refused(string payload) =>
        CommandEvidenceRefs.Read(Json(payload.Replace("HASH", Hash, StringComparison.Ordinal))).Should().BeNull();

    [Fact]
    public void A_command_names_at_most_20_blobs()
    {
        var many = string.Join(',', Enumerable.Range(0, CommandEvidenceRefs.MaxPerCommand + 1)
            .Select(i => $$"""{"sha256":"{{Convert.ToHexStringLower(SHA256.HashData([(byte)i]))}}","size":10,"contentType":"image/png"}"""));

        CommandEvidenceRefs.Read(Json($$"""{"evidence":[{{many}}]}""")).Should().BeNull();
    }

    [Fact]
    public void The_key_is_the_tablet_s_own_and_named_by_the_content()
    {
        var tenant = Guid.NewGuid();
        var device = Guid.NewGuid();

        EvidenceKeys.For(tenant, device, Hash).Should().Be($"tenants/{tenant:D}/devices/{device:D}/evidence/{Hash}");
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
