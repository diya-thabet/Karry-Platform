using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Canonical;

// Patterns end in \z and use [0-9], not $ and \d: in .NET, $ also matches before a final newline and \d matches
// any Unicode digit, unlike the same patterns in JavaScript.

namespace Karry.Modules.Trust.Contracts;

/// <summary>
/// Signature envelope v1 (ADR-0033 §1): the members a tablet signs for a record. Optional members are absent, never
/// <c>null</c>, and no other member is allowed, so the canonical text is unambiguous; a new member means envelope v2.
/// The rules are those of <c>envelopeProblem</c> in <c>@karry/crypto</c>, and both pass
/// <c>tests/vectors/signature-envelope.json</c> (an independent Python implementation), so a tablet never signs an
/// envelope the server would refuse.
/// </summary>
public static partial class SignatureEnvelopes
{
    public const int MaxEvidence = 20;

    private static readonly string[] Top = ["v", "envelopeId", "recordType", "recordId", "recordVersion", "payloadHash", "signer", "context"];
    private static readonly string[] TopOptional = ["evidence", "handwritten"];

    /// <summary>Why <paramref name="envelope"/> is not a valid envelope v1 (a short rule name), or <c>null</c> when it is.</summary>
    public static string? Problem(JsonElement envelope)
    {
        if (!Shape(envelope, Top, TopOptional))
        {
            return "members";
        }

        if (envelope.GetProperty("v") is not { ValueKind: JsonValueKind.Number } v || !v.TryGetInt32(out var version) || version != 1)
        {
            return "v";
        }

        if (!IsUuid(envelope.GetProperty("envelopeId")) || !IsUuid(envelope.GetProperty("recordId")))
        {
            return "id";
        }

        if (!Matches(envelope.GetProperty("recordType"), RecordType(), 100))
        {
            return "recordType";
        }

        if (!IsSafeInteger(envelope.GetProperty("recordVersion"), out var recordVersion) || recordVersion < 1)
        {
            return "recordVersion";
        }

        if (!IsHash(envelope.GetProperty("payloadHash")))
        {
            return "payloadHash";
        }

        if (envelope.TryGetProperty("evidence", out var evidence)
            && (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() is 0 or > MaxEvidence
                || !evidence.EnumerateArray().All(IsHash) || evidence.EnumerateArray().Select(e => e.GetString()).Distinct(StringComparer.Ordinal).Count() != evidence.GetArrayLength()))
        {
            return "evidence";
        }

        if (envelope.TryGetProperty("handwritten", out var handwritten)
            && (!Shape(handwritten, ["strokesHash", "imageHash"]) || !IsHash(handwritten.GetProperty("strokesHash")) || !IsHash(handwritten.GetProperty("imageHash"))))
        {
            return "handwritten";
        }

        var signer = envelope.GetProperty("signer");
        if (!Shape(signer, ["userId", "role", "deviceId"]) || !IsUuid(signer.GetProperty("userId")) || !IsUuid(signer.GetProperty("deviceId"))
            || !IsText(signer.GetProperty("role"), 50))
        {
            return "signer";
        }

        var context = envelope.GetProperty("context");
        if (!Shape(context, ["deviceTime", "appVersion"], ["geo"]) || !Matches(context.GetProperty("deviceTime"), DeviceTime(), 40)
            || !IsText(context.GetProperty("appVersion"), 40))
        {
            return "context";
        }

        if (context.TryGetProperty("geo", out var geo)
            && (!Shape(geo, ["lat", "lon", "acc"]) || !InRange(geo.GetProperty("lat"), -90, 90) || !InRange(geo.GetProperty("lon"), -180, 180)
                || !InRange(geo.GetProperty("acc"), 0, 100_000)))
        {
            return "geo";
        }

        return null;
    }

    /// <summary>The RFC 8785 text the device signed (UTF-8).</summary>
    public static byte[] Canonical(JsonElement envelope) => JsonCanonicalizer.Canonicalize(envelope);

    /// <summary><c>sha256:</c> + hex of SHA-256 over the canonical form: how the ledger and the countersignature name it.</summary>
    public static string Hash(JsonElement envelope) => HashOf(Canonical(envelope));

    /// <summary>The <c>payloadHash</c> of a record's payload: SHA-256 over its RFC 8785 form.</summary>
    public static string PayloadHash(JsonElement payload) => HashOf(JsonCanonicalizer.Canonicalize(payload));

    /// <summary>
    /// Whether <paramref name="deviceSignature"/> (ES256, IEEE P1363, base64url) is the device's signature over the
    /// envelope; a malformed envelope never verifies. <paramref name="x"/> and <paramref name="y"/> are the device's
    /// P-256 public point, as stored at enrolment.
    /// </summary>
    public static bool VerifyDevice(JsonElement envelope, ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, string deviceSignature)
    {
        ArgumentNullException.ThrowIfNull(deviceSignature);
        if (Problem(envelope) is not null)
        {
            return false;
        }

        Span<byte> signature = stackalloc byte[75];
        if (Base64Url.DecodeFromChars(deviceSignature, signature, out var consumed, out var written) != System.Buffers.OperationStatus.Done
            || consumed != deviceSignature.Length || written != 64)
        {
            return false;
        }

        using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x.ToArray(), Y = y.ToArray() } });
        return key.VerifyData(Canonical(envelope), signature[..64], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static string HashOf(byte[] canonical) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(canonical));

    /// <summary>The members of <paramref name="value"/> are exactly <paramref name="required"/> plus any of <paramref name="optional"/>.</summary>
    private static bool Shape(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = value.EnumerateObject().Select(p => p.Name).ToList();
        return required.All(names.Contains) && names.All(n => required.Contains(n) || (optional?.Contains(n) ?? false));
    }

    private static bool IsText(JsonElement value, int max) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= max;

    private static bool Matches(JsonElement value, Regex pattern, int max) =>
        IsText(value, max) && pattern.IsMatch(value.GetString()!);

    private static bool IsUuid(JsonElement value) => Matches(value, Uuid(), 36);

    private static bool IsHash(JsonElement value) => Matches(value, ContentHash(), 71);

    private static bool IsSafeInteger(JsonElement value, out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsInteger(number)
            && Math.Abs(number) <= 9_007_199_254_740_991 && (result = (long)number) == number;
    }

    private static bool InRange(JsonElement value, double min, double max) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= min && number <= max;

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    [GeneratedRegex("^sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContentHash();

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RecordType();

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceTime();
}
