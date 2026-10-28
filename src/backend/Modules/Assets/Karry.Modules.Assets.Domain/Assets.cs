using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Assets.Domain;

public readonly record struct AssetId(Guid Value) : IEntityId<AssetId>
{
    public static AssetId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>Where an asset stands (US-P1-AST-02 AC1).</summary>
public enum AssetStatus
{
    Active = 1,
    InRepair = 2,
    Idle = 3,

    /// <summary>Sold or scrapped: kept with its history, out of the default lists and the asset-count meter.</summary>
    Disposed = 4,
}

/// <summary>What describes an asset, validated (<see cref="AssetRules.Details"/>); the code is apart because it never changes.</summary>
public sealed record AssetDetails(
    string Name, string Type, string? Make, string? Model, string? SerialNumber, string? Plate, int? Year, DateOnly? AcquiredOn);

/// <summary>
/// One arrival of an asset at a site (bounded contexts §9.4, ADR-0026): the stay lasts until the next arrival. Rows are
/// append-only (trigger and grants).
/// </summary>
public sealed class SiteAssignment
{
    internal SiteAssignment(TenantId tenantId, Guid siteId, DateTimeOffset from)
    {
        TenantId = tenantId;
        SiteId = siteId;
        From = from;
    }

    // EF Core
    private SiteAssignment()
    {
    }

    public TenantId TenantId { get; private set; }

    public Guid SiteId { get; private set; }

    public DateTimeOffset From { get; private set; }
}

/// <summary>The asset as stored in the ledger.</summary>
public sealed record AssetSnapshot(
    string Code, string Name, string Type, string? Make, string? Model, string? SerialNumber, string? Plate, int? Year, DateOnly? AcquiredOn);

[EventType("assets.asset_registered.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record AssetRegistered(Guid AssetId, AssetSnapshot Asset, Guid SiteId, string? Actor) : DomainEvent;

[EventType("assets.asset_changed.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record AssetChanged(Guid AssetId, AssetSnapshot Before, AssetSnapshot After, string? Actor) : DomainEvent;

[EventType("assets.asset_moved.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record AssetMoved(Guid AssetId, string Code, Guid FromSiteId, Guid ToSiteId, string? Actor) : DomainEvent;

/// <summary>A new label token: the printed label before it no longer resolves. The token itself is not in the ledger.</summary>
[EventType("assets.asset_label_reissued.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record AssetLabelReissued(Guid AssetId, string Code, string? Actor) : DomainEvent;

/// <summary>The asset's photo changed: its SHA-256 fingerprint, or null when it was removed.</summary>
[EventType("assets.asset_photo_changed.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record AssetPhotoChanged(Guid AssetId, string Code, string? Sha256, string? Actor) : DomainEvent;

[EventType("assets.asset_status_changed.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record AssetStatusChanged(Guid AssetId, string Code, string From, string To, string? Actor) : DomainEvent;

/// <summary>
/// A machine of the company (US-P1-AST-02): one identity for its whole life, a current site with its site history, and
/// a status. Tenant-owned (RLS). Disposed, never deleted: its operations keep pointing at it.
/// </summary>
public sealed class Asset : AggregateRoot<AssetId>, ITenantOwned
{
    private readonly List<SiteAssignment> sites = [];

    private Asset(AssetId id, TenantId tenantId, string code, AssetDetails details, Guid siteId, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        Code = code;
        Apply(details);
        SiteId = siteId;
        LabelToken = AssetLabels.NewToken();
        sites.Add(new SiteAssignment(tenantId, siteId, at));
        Status = AssetStatus.Active;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private Asset()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    /// <summary>Internal code, unique in the company for ever (disposed assets keep theirs), e.g. "PEL-01".</summary>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>A code of <see cref="AssetTypes"/>.</summary>
    public string Type { get; private set; } = null!;

    public string? Make { get; private set; }

    public string? Model { get; private set; }

    /// <summary>Serial number or VIN.</summary>
    public string? SerialNumber { get; private set; }

    /// <summary>Registration plate, for machines on public roads.</summary>
    public string? Plate { get; private set; }

    public int? Year { get; private set; }

    public DateOnly? AcquiredOn { get; private set; }

    /// <summary>The site the asset is on now: the last of <see cref="Sites"/>.</summary>
    public Guid SiteId { get; private set; }

    /// <summary>Arrivals at sites, oldest first; never empty.</summary>
    public IReadOnlyList<SiteAssignment> Sites => sites;

    public AssetStatus Status { get; private set; }

    /// <summary>The current photo in object storage (<see cref="AssetPhotos.Key"/>), null when there is none.</summary>
    public AssetPhoto? Photo { get; private set; }

    /// <summary>The random token on the asset's QR label (ADR-0026): not derived from the ID, replaced on reissue.</summary>
    public string LabelToken { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public DateTimeOffset? DisposedAt { get; private set; }

    public static Asset Register(TimeProvider clock, TenantId tenant, string code, AssetDetails details, Guid siteId, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        var at = clock.GetUtcNow();
        var asset = new Asset(EntityId.New<AssetId>(clock), tenant, code, details, siteId, at);
        asset.Raise(new AssetRegistered(asset.Id.Value, asset.Snapshot(), siteId, actor) { OccurredAt = at });
        return asset;
    }

    /// <summary>New details; false when nothing changed (no ledger entry). A disposed asset is not changed.</summary>
    public Result<bool> Change(TimeProvider clock, AssetDetails details, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        if (Status == AssetStatus.Disposed)
        {
            return AssetRules.Disposed;
        }

        var before = Snapshot();
        Apply(details);
        if (Snapshot() == before)
        {
            return false;
        }

        ChangedAt = clock.GetUtcNow();
        Raise(new AssetChanged(Id.Value, before, Snapshot(), actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Moves the asset to another site (a new arrival in its history); false when it is already there.</summary>
    public Result<bool> MoveTo(TimeProvider clock, Guid siteId, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == AssetStatus.Disposed)
        {
            return AssetRules.Disposed;
        }

        if (siteId == SiteId)
        {
            return false;
        }

        var from = SiteId;
        ChangedAt = clock.GetUtcNow();
        SiteId = siteId;
        sites.Add(new SiteAssignment(TenantId, siteId, ChangedAt));
        Raise(new AssetMoved(Id.Value, Code, from, siteId, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>A new label token for a lost or damaged label; the old label stops resolving.</summary>
    public void ReissueLabel(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        LabelToken = AssetLabels.NewToken();
        ChangedAt = clock.GetUtcNow();
        Raise(new AssetLabelReissued(Id.Value, Code, actor) { OccurredAt = ChangedAt });
    }

    /// <summary>The photo becomes <paramref name="photo"/> (null removes it); false when unchanged. A disposed asset keeps its photo.</summary>
    public Result<bool> SetPhoto(TimeProvider clock, AssetPhoto? photo, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == AssetStatus.Disposed)
        {
            return AssetRules.Disposed;
        }

        if (photo?.Sha256 == Photo?.Sha256)
        {
            return false;
        }

        Photo = photo;
        ChangedAt = clock.GetUtcNow();
        Raise(new AssetPhotoChanged(Id.Value, Code, photo?.Sha256, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>New status; false when unchanged. Leaving <see cref="AssetStatus.Disposed"/> counts the asset again.</summary>
    public bool SetStatus(TimeProvider clock, AssetStatus status, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (status == Status)
        {
            return false;
        }

        var from = Status;
        Status = status;
        ChangedAt = clock.GetUtcNow();
        DisposedAt = status == AssetStatus.Disposed ? ChangedAt : null;
        Raise(new AssetStatusChanged(Id.Value, Code, AssetRules.StatusCode(from), AssetRules.StatusCode(status), actor) { OccurredAt = ChangedAt });
        return true;
    }

    private void Apply(AssetDetails details)
    {
        Name = details.Name;
        Type = details.Type;
        Make = details.Make;
        Model = details.Model;
        SerialNumber = details.SerialNumber;
        Plate = details.Plate;
        Year = details.Year;
        AcquiredOn = details.AcquiredOn;
    }

    private AssetSnapshot Snapshot() => new(Code, Name, Type, Make, Model, SerialNumber, Plate, Year, AcquiredOn);
}

/// <summary>Asset input rules (US-P1-AST-02 AC1).</summary>
public static partial class AssetRules
{
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 100;
    public const int MakeMaxLength = 60;
    public const int SerialMaxLength = 40;
    public const int PlateMaxLength = 20;

    /// <summary>The oldest year and acquisition date accepted: older entries are typing mistakes.</summary>
    public const int FirstYear = 1950;

    public static readonly Error CodeInvalid = Error.Validation(
        "assets.code_invalid", "A code is 1 to 20 capital letters, digits, dashes, dots or slashes, starting with a letter or digit.");

    public static readonly Error NameInvalid = Error.Validation("assets.name_invalid", $"A name is 1 to {NameMaxLength} characters.");

    public static readonly Error TypeInvalid = Error.Validation("assets.type_invalid", "Choose a type from the catalogue.");

    public static readonly Error StatusInvalid = Error.Validation("assets.status_invalid", "The status is active, in-repair, idle or disposed.");

    public static readonly Error Disposed = Error.Conflict("assets.asset_disposed", "A disposed asset keeps its details; bring it back into use first.");

    public static Error TextTooLong(string field, int max) =>
        Error.Validation("assets.text_too_long", $"The {field} is at most {max} characters.").With("field", field).With("max", max);

    public static Error YearInvalid(int last) =>
        Error.Validation("assets.year_invalid", $"The year is from {FirstYear} to {last}.").With("last", last);

    public static Error AcquiredOnInvalid(DateOnly today) =>
        Error.Validation("assets.acquired_on_invalid", $"The acquisition date is from {FirstYear}-01-01 to today.")
            .With("today", today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    public static Result<string> Code(string? code)
    {
        var value = code?.Trim().ToUpperInvariant();
        return value is not null && CodePattern().IsMatch(value) ? value : CodeInvalid;
    }

    /// <summary>
    /// Validates and normalizes what describes an asset: blanks become null, serial numbers and plates are upper-case
    /// with single spaces, the year is up to next year (a machine ordered now) and the acquisition date up to today.
    /// </summary>
    public static Result<AssetDetails> Details(
        string? name, string? type, string? make, string? model, string? serialNumber, string? plate, int? year, DateOnly? acquiredOn, DateOnly today)
    {
        var trimmedName = name?.Trim();
        if (trimmedName is not { Length: > 0 and <= NameMaxLength })
        {
            return NameInvalid;
        }

        if (AssetTypes.Find(type) is null)
        {
            return TypeInvalid;
        }

        foreach (var (field, value, max) in new[] { ("make", make, MakeMaxLength), ("model", model, MakeMaxLength), ("serialNumber", serialNumber, SerialMaxLength), ("plate", plate, PlateMaxLength) })
        {
            if (Optional(value)?.Length > max)
            {
                return TextTooLong(field, max);
            }
        }

        if (year is { } y && (y < FirstYear || y > today.Year + 1))
        {
            return YearInvalid(today.Year + 1);
        }

        if (acquiredOn is { } on && (on.Year < FirstYear || on > today))
        {
            return AcquiredOnInvalid(today);
        }

        return new AssetDetails(trimmedName, type!, Optional(make), Optional(model), Optional(serialNumber)?.ToUpperInvariant(), Optional(plate)?.ToUpperInvariant(), year, acquiredOn);
    }

    public static Result<AssetStatus> Status(string? status) => status switch
    {
        "active" => AssetStatus.Active,
        "in-repair" => AssetStatus.InRepair,
        "idle" => AssetStatus.Idle,
        "disposed" => AssetStatus.Disposed,
        _ => StatusInvalid,
    };

    public static string StatusCode(AssetStatus status) => status switch
    {
        AssetStatus.Active => "active",
        AssetStatus.InRepair => "in-repair",
        AssetStatus.Idle => "idle",
        AssetStatus.Disposed => "disposed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static string MeterKindCode(MeterKind kind) => kind switch
    {
        MeterKind.Hours => "hours",
        MeterKind.Km => "km",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Trimmed with inner runs of spaces collapsed; null when blank.</summary>
    private static string? Optional(string? value)
    {
        var text = value is null ? null : Spaces().Replace(value.Trim(), " ");
        return string.IsNullOrEmpty(text) ? null : text;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9./-]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}

/// <summary>
/// What an asset's QR label encodes (ADR-0026): <c>KRY1:</c> and a 128-bit random token in base32 (26 characters). It
/// says nothing about the asset or the company; it resolves only inside the company's register.
/// </summary>
public static partial class AssetLabels
{
    public const string Prefix = "KRY1:";
    public const int TokenLength = 26;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Text(string token) => Prefix + token;

    /// <summary>The token of a scanned label (with or without the prefix, any case), or null when it cannot be one.</summary>
    public static string? Token(string? label)
    {
        var text = label?.Trim().ToUpperInvariant();
        if (text is not null && text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            text = text[Prefix.Length..];
        }

        return text is not null && TokenPattern().IsMatch(text) ? text : null;
    }

    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var token = new char[TokenLength];
        int buffer = 0, bits = 0, i = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                token[i++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }

        token[i] = Alphabet[(buffer << (5 - bits)) & 31];
        return new string(token);
    }

    [GeneratedRegex("^[A-Z2-7]{26}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

/// <summary>An asset photo as stored: fingerprint (it names the object), media type and size.</summary>
public sealed record AssetPhoto(string Sha256, string ContentType, int Bytes);

/// <summary>Asset photos (US-P1-AST-02 AC1): JPEG, PNG or WebP up to 5 MB, the type read from the content, never from the request.</summary>
public static class AssetPhotos
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static readonly Error TypeUnsupported = Error.Validation("assets.photo_type_unsupported", "A photo is a JPEG, PNG or WebP image.");

    public static readonly Error TooLarge = Error.Validation("assets.photo_too_large", "A photo is at most 5 MB.").With("maxBytes", MaxBytes);

    public static readonly Error NotFound = Error.NotFound("assets.photo_not_found", "This asset has no photo.");

    public static readonly Error Unavailable = Error.BusinessRule("assets.photos_unavailable", "Photo storage is not configured on this server.");

    /// <summary>The object key under the company's prefix: the fingerprint changes with the photo, so a new photo never overwrites the old one.</summary>
    public static string Key(AssetId asset, AssetPhoto photo) => $"{asset}/photo-{photo.Sha256}";

    public static Result<AssetPhoto> Create(ReadOnlySpan<byte> content)
    {
        if (content.Length > MaxBytes)
        {
            return TooLarge;
        }

        if (Sniff(content) is not { } type)
        {
            return TypeUnsupported;
        }

        return new AssetPhoto(Convert.ToHexStringLower(SHA256.HashData(content)), type, content.Length);
    }

    public static string? Sniff(ReadOnlySpan<byte> content) => content switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null,
    };
}
