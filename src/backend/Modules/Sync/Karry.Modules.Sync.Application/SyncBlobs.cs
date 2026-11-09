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

    public static readonly Error Unavailable = Error.BusinessRule("sync.blobs_unavailable", "Evidence storage is not configured on this server.");

    /// <summary>64 lowercase hex characters: the key is built from it, so nothing else may get into a path.</summary>
    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>The checksum header's form of the same hash (base64 of the 32 bytes).</summary>
    public static string ToBase64(string sha256Hex) => Convert.ToBase64String(Convert.FromHexString(sha256Hex));
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
