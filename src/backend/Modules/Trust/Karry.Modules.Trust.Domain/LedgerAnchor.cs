using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Karry.SharedKernel.Canonical;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Trust.Domain;

/// <summary>
/// A tenant's chain head written to WORM storage (US-P1-AUD-02, ADR-0008). Stored object (RFC 8785 canonical JSON):
/// <code>{"alg":"ES256","anchor":{"anchoredAt":…,"entryHash":…,"keyId":…,"seq":…,"tenantId":…},"signature":base64(P1363)}</code>
/// The signature covers the canonical bytes of <c>anchor</c>. Object key: <c>{tenantId}/{seq:D20}.json</c>, so keys sort
/// by sequence and a tenant's anchors are listed by prefix.
/// </summary>
public sealed record LedgerAnchor(TenantId Tenant, long Sequence, string EntryHash, DateTimeOffset AnchoredAt, string KeyId)
{
    public const string Algorithm = "ES256";

    public string ObjectKey => $"{Tenant}/{Sequence.ToString("D20", CultureInfo.InvariantCulture)}.json";

    /// <summary>The signed bytes: canonical JSON of the anchor fields.</summary>
    public byte[] SignedBytes() => JsonCanonicalizer.Canonicalize(JsonSerializer.Serialize(new Body(
        Tenant.ToString(), Sequence, EntryHash, LedgerHash.Timestamp(AnchoredAt), KeyId)));

    public string ToDocument(byte[] signature) => JsonCanonicalizer.CanonicalText(JsonSerializer.Serialize(new Document(
        Algorithm, new Body(Tenant.ToString(), Sequence, EntryHash, LedgerHash.Timestamp(AnchoredAt), KeyId), Convert.ToBase64String(signature))));

    /// <summary>Parses a stored object; null when it is not a well-formed anchor document.</summary>
    public static (LedgerAnchor Anchor, byte[] Signature)? FromDocument(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<Document>(json);
            if (document?.Anchor is not { } body || document.Alg != Algorithm || !Guid.TryParseExact(body.TenantId, "D", out var tenant))
            {
                return null;
            }

            var anchoredAt = DateTimeOffset.ParseExact(body.AnchoredAt, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return (new LedgerAnchor(new TenantId(tenant), body.Seq, body.EntryHash, anchoredAt, body.KeyId), Convert.FromBase64String(document.Signature));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private sealed record Body(
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("seq")] long Seq,
        [property: JsonPropertyName("entryHash")] string EntryHash,
        [property: JsonPropertyName("anchoredAt")] string AnchoredAt,
        [property: JsonPropertyName("keyId")] string KeyId);

    private sealed record Document(
        [property: JsonPropertyName("alg")] string Alg,
        [property: JsonPropertyName("anchor")] Body? Anchor,
        [property: JsonPropertyName("signature")] string Signature);
}
