using System.Security.Cryptography;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Tenancy.Domain;

/// <summary>
/// A company's logo (US-P1-TEN-04 AC1), shown in the apps and on the public sign-in page (threat model R-07). Only PNG,
/// JPEG and WebP, recognised by their content (never by the declared type), at most 256 KB; SVG is refused because it
/// can carry script.
/// </summary>
public sealed class TenantLogo
{
    public const int MaxBytes = 256 * 1024;

    private TenantLogo(Guid tenantId, string contentType, byte[] content, string sha256, DateTimeOffset changedAt)
    {
        TenantId = tenantId;
        ContentType = contentType;
        Content = content;
        Sha256 = sha256;
        ChangedAt = changedAt;
    }

    public Guid TenantId { get; private set; }

    public string ContentType { get; private set; }

    public byte[] Content { get; private set; }

    /// <summary>Lowercase hex; also the logo's version in its address and ETag.</summary>
    public string Sha256 { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public static Result<TenantLogo> Create(Guid tenantId, ReadOnlySpan<byte> content, DateTimeOffset at)
    {
        if (content.Length > MaxBytes)
        {
            return Error.Validation("tenant.logo_too_large", $"The logo must be at most {MaxBytes / 1024} KB.").With("maxBytes", MaxBytes);
        }

        var type = Sniff(content);
        return type is null
            ? Error.Validation("tenant.logo_type_unsupported", "The logo must be a PNG, JPEG or WebP image.")
            : new TenantLogo(tenantId, type, content.ToArray(), Convert.ToHexStringLower(SHA256.HashData(content)), at);
    }

    /// <summary>The image type from its first bytes, or null.</summary>
    public static string? Sniff(ReadOnlySpan<byte> content) => content switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null,
    };
}
