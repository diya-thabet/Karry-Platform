using System.Text.Json;
using FluentValidation;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Storage;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Sync.Application;

/// <summary>A blob the tablet wants to upload: its SHA-256 (64 lowercase hex characters), size in bytes and media type.</summary>
public sealed record PresignBlob(string? Sha256, long Size, string? ContentType);

/// <summary>
/// The answer: <see cref="Exists"/> when the store already holds this content (a retry; nothing to upload), else the
/// URL to <c>PUT</c> the bytes to, the headers to send with them exactly as given, and until when the URL works.
/// </summary>
public sealed record BlobUpload(bool Exists, string Key, Uri? Url, IReadOnlyDictionary<string, string>? Headers, DateTimeOffset? ExpiresAt);

public static class SyncBlobRules
{
    /// <summary>At most 1 MB, so no multipart (ADR-0032 §3): photos are compressed to about 200 KB on the tablet.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>Photos, and signature strokes as JSON (ADR-0032 §2).</summary>
    public static readonly IReadOnlySet<string> ContentTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "image/jpeg", "image/webp", "image/png", "application/json",
    };

    public const string UnavailableCode = "sync.blobs_unavailable";

    /// <summary>A blob the command names is not in the store (yet): the tablet uploads it and sends the command again.</summary>
    public const string BlobMissing = "sync.blob_missing";

    /// <summary>The store holds other content than the command names (size, checksum or type): never accepted.</summary>
    public const string BlobMismatch = "sync.blob_mismatch";

    /// <summary>The payload's <c>evidence</c> member is malformed.</summary>
    public const string EvidenceInvalid = "sync.evidence_invalid";

    public static readonly Error Unavailable = Error.BusinessRule(UnavailableCode, "Evidence storage is not configured on this server.");

    /// <summary>64 lowercase hex characters: the key is built from it, so nothing else may get into a path.</summary>
    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>The checksum header's form of the same hash (base64 of the 32 bytes).</summary>
    public static string ToBase64(string sha256Hex) => Convert.ToBase64String(Convert.FromHexString(sha256Hex));
}

/// <summary>A blob a command's signed payload names: what the store must hold, byte for byte (ADR-0032 §1).</summary>
public sealed record EvidenceRef(string Sha256, long Size, string ContentType);

/// <summary>
/// The <c>evidence</c> member of a command's payload: an array of <c>{sha256, size, contentType}</c> (other members,
/// such as the capture record, are the handler's). No member means no evidence.
/// </summary>
public static class CommandEvidenceRefs
{
    public const string Member = "evidence";

    /// <summary>More blobs than any field form needs (a few photos and a signature).</summary>
    public const int MaxPerCommand = 20;

    /// <summary>The references, or null when the member is there but malformed (the command is rejected for it).</summary>
    public static IReadOnlyList<EvidenceRef>? Read(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(Member, out var evidence))
        {
            return [];
        }

        if (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() > MaxPerCommand)
        {
            return null;
        }

        var refs = new List<EvidenceRef>();
        foreach (var item in evidence.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("sha256", out var sha256) || sha256.ValueKind != JsonValueKind.String || !SyncBlobRules.IsSha256Hex(sha256.GetString())
                || !item.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes is < 1 or > SyncBlobRules.MaxBytes
                || !item.TryGetProperty("contentType", out var type) || type.ValueKind != JsonValueKind.String || !SyncBlobRules.ContentTypes.Contains(type.GetString()!))
            {
                return null;
            }

            refs.Add(new EvidenceRef(sha256.GetString()!, bytes, type.GetString()!));
        }

        // The same blob named twice is one blob; the same hash with another size or type is a malformed payload.
        var distinct = refs.Distinct().ToList();
        return distinct.DistinctBy(r => r.Sha256).Count() == distinct.Count ? distinct : null;
    }
}

public sealed class PresignBlobValidator : AbstractValidator<PresignBlob>
{
    public PresignBlobValidator()
    {
        RuleFor(b => b.Sha256).Must(SyncBlobRules.IsSha256Hex).WithMessage("The SHA-256 of the content is required, as 64 lowercase hex characters.");
        RuleFor(b => b.Size).InclusiveBetween(1, SyncBlobRules.MaxBytes).WithMessage($"A blob holds 1 to {SyncBlobRules.MaxBytes} bytes.");
        RuleFor(b => b.ContentType).Must(t => t is not null && SyncBlobRules.ContentTypes.Contains(t))
            .WithMessage($"The type must be one of {string.Join(", ", SyncBlobRules.ContentTypes)}.");
    }
}

/// <summary>
/// Pre-signed uploads of evidence blobs (ADR-0032 §2). The key is the tablet's own (its tenant and device from the
/// signed request, never from the body), so a tablet cannot obtain a URL for another company's or tablet's key; the
/// URL is valid for that key, that checksum and type, and a short time. The bytes never pass through the API.
/// </summary>
public sealed class SyncBlobs(IEvidenceStore store, EvidenceStoreOptions options, ITenantContext tenant, TimeProvider clock)
{
    public async Task<Result<BlobUpload>> PresignAsync(DeviceCaller caller, PresignBlob blob, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (!store.Available)
        {
            return SyncBlobRules.Unavailable;
        }

        var tenantId = tenant.Current ?? throw new InvalidOperationException("A device request always runs for its tenant.");
        var key = EvidenceKeys.For(tenantId.Value, caller.DeviceId, blob.Sha256!);
        var sha256 = SyncBlobRules.ToBase64(blob.Sha256!);
        if (await store.HeadAsync(key, cancellationToken).ConfigureAwait(false) is { } head && head.Sha256 == sha256 && head.Size == blob.Size)
        {
            return new BlobUpload(true, key, null, null, null);
        }

        var put = store.PresignPut(key, sha256, blob.ContentType!, clock.GetUtcNow() + options.UploadValidFor);
        return new BlobUpload(false, key, put.Url, put.Headers, put.ExpiresAt);
    }
}
