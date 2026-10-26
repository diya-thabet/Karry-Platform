using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Tenancy.Domain;

public enum TenantStatus
{
    /// <summary>Provisioned and usable. Trial, restricted and suspended states arrive with subscriptions (P1.10).</summary>
    Active = 1,
}

/// <summary>Raised once when a tenant is provisioned; other modules create their defaults from it (P1.8–P1.11).</summary>
[EventType("tenancy.tenant_provisioned.v1")]
public sealed record TenantProvisioned(Guid TenantId, string Slug, string Name) : DomainEvent;

/// <summary>A change of the company's settings (US-P1-TEN-04 AC2): company data only, no personal data.</summary>
[EventType("tenancy.tenant_settings_changed.v1")]
public sealed record TenantSettingsChanged(Guid TenantId, TenantSettings Before, TenantSettings After, string? Actor) : DomainEvent;

/// <summary>The logo was replaced or removed; the ledger keeps its fingerprint, not the image.</summary>
[EventType("tenancy.tenant_logo_changed.v1")]
public sealed record TenantLogoChanged(Guid TenantId, string? BeforeSha256, string? AfterSha256, string? ContentType, int? Bytes, string? Actor) : DomainEvent;

/// <summary>
/// A customer company in the tenant catalog (US-P1-TEN-01). The catalog row itself is platform data, not tenant-owned:
/// it is read to resolve addresses and by operators, and holds no business data of the tenant.
/// </summary>
public sealed class Tenant : AggregateRoot<TenantId>, ITenantAudited
{
    public const int NameMaxLength = 120;

    private Tenant(TenantId id, TenantSlug slug, string name, DateTimeOffset createdAt)
        : base(id)
    {
        Slug = slug;
        Name = name;
        CreatedAt = createdAt;
        Status = TenantStatus.Active;
        ApplySettings(TenantSettings.DefaultsFor(name));
    }

    public TenantSlug Slug { get; private set; }

    public string Name { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string LegalName { get; private set; } = null!;

    /// <summary>#RRGGBB, or null for Karry's own colour.</summary>
    public string? PrimaryColour { get; private set; }

    public string Language { get; private set; } = null!;

    public string Currency { get; private set; } = null!;

    public UnitSystem UnitSystem { get; private set; }

    /// <summary>IANA time zone; a site may use another one.</summary>
    public string TimeZone { get; private set; } = null!;

    public int FiscalYearStartMonth { get; private set; }

    /// <summary>The logo's fingerprint (<see cref="TenantLogo.Sha256"/>), null without a logo.</summary>
    public string? LogoSha256 { get; private set; }

    public DateTimeOffset? SettingsChangedAt { get; private set; }

    public TenantSettings Settings => new(LegalName, PrimaryColour, Language, Currency, UnitSystem, TimeZone, FiscalYearStartMonth);

    /// <summary>The tenant's own ledger starts with its provisioning.</summary>
    TenantId ITenantAudited.AuditTenantId => Id;

    public static Result<Tenant> Provision(TimeProvider clock, string? slug, string? name)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var validSlug = TenantSlug.Create(slug);
        if (!validSlug.IsSuccess)
        {
            return validSlug.Error;
        }

        var validName = NormalizeName(name);
        if (!validName.IsSuccess)
        {
            return validName.Error;
        }

        var tenant = new Tenant(EntityId.New<TenantId>(clock), validSlug.Value, validName.Value, clock.GetUtcNow());
        tenant.Raise(new TenantProvisioned(tenant.Id.Value, tenant.Slug.Value, tenant.Name) { OccurredAt = tenant.CreatedAt });
        return tenant;
    }

    /// <summary>Applies new settings; false when nothing changed (no event, no ledger entry).</summary>
    public bool ChangeSettings(TenantSettings settings, string? actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var before = Settings;
        if (before == settings)
        {
            return false;
        }

        ApplySettings(settings);
        SettingsChangedAt = at;
        Raise(new TenantSettingsChanged(Id.Value, before, settings, actor) { OccurredAt = at });
        return true;
    }

    /// <summary>Records a new logo (or none); false when it is the same image.</summary>
    public bool ChangeLogo(TenantLogo? logo, string? actor, DateTimeOffset at)
    {
        if (logo?.Sha256 == LogoSha256)
        {
            return false;
        }

        var before = LogoSha256;
        LogoSha256 = logo?.Sha256;
        SettingsChangedAt = at;
        Raise(new TenantLogoChanged(Id.Value, before, LogoSha256, logo?.ContentType, logo?.Content.Length, actor) { OccurredAt = at });
        return true;
    }

    private void ApplySettings(TenantSettings settings)
    {
        LegalName = settings.LegalName;
        PrimaryColour = settings.PrimaryColour;
        Language = settings.Language;
        Currency = settings.Currency;
        UnitSystem = settings.UnitSystem;
        TimeZone = settings.TimeZone;
        FiscalYearStartMonth = settings.FiscalYearStartMonth;
    }

    /// <summary>Trimmed, inner whitespace collapsed, 2–120 characters, no control characters.</summary>
    public static Result<string> NormalizeName(string? name)
    {
        var value = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (value.Length is < 2 or > NameMaxLength || value.Any(char.IsControl))
        {
            return Error.Validation("tenant.name_invalid", $"The company name must be 2–{NameMaxLength} characters.");
        }

        return value;
    }
}
