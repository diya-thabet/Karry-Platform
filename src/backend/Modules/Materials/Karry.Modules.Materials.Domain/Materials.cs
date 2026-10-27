using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Karry.SharedKernel.Units;

namespace Karry.Modules.Materials.Domain;

public readonly record struct MaterialId(Guid Value) : IEntityId<MaterialId>
{
    public static MaterialId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>What a material is (US-P1-AST-04 AC1): rock as extracted, or a product sold (a fraction such as 8/16).</summary>
public enum MaterialKind
{
    RawRock = 1,
    Product = 2,
}

public enum MaterialStatus
{
    Active = 1,

    /// <summary>No longer produced or sold: kept with its history (past conversions still work), out of the default lists.</summary>
    Archived = 2,
}

/// <summary>
/// The densities valid from a date (US-P1-AST-04 AC2, ADR-0025): dry density in t/m³ per state the material has, and
/// the default moisture (mass fraction). Valid until the next version.
/// </summary>
public sealed record DensityVersion(DateOnly EffectiveFrom, decimal? Bank, decimal? Loose, decimal? Compacted, decimal Moisture)
{
    /// <summary>Derived, so never serialized: its swell can have 28 digits (2.65 / 1.70 − 1), which the ledger refuses as a number.</summary>
    [JsonIgnore]
    public MaterialDensities Densities => new(Bank, Loose, Compacted, Moisture);
}

/// <summary>The material as stored in the ledger.</summary>
public sealed record MaterialSnapshot(string Code, string Name, string Kind);

[EventType("materials.material_created.v1")]
public sealed record MaterialCreated(Guid MaterialId, MaterialSnapshot Material, DensityVersion Densities, string? Actor) : DomainEvent;

[EventType("materials.material_changed.v1")]
public sealed record MaterialChanged(Guid MaterialId, MaterialSnapshot Before, MaterialSnapshot After, string? Actor) : DomainEvent;

[EventType("materials.material_archived.v1")]
public sealed record MaterialArchived(Guid MaterialId, string Code, string? Actor) : DomainEvent;

[EventType("materials.material_restored.v1")]
public sealed record MaterialRestored(Guid MaterialId, string Code, string? Actor) : DomainEvent;

[EventType("materials.density_version_set.v1")]
public sealed record DensityVersionSet(Guid MaterialId, string Code, DensityVersion? Before, DensityVersion After, string? Actor) : DomainEvent;

[EventType("materials.density_version_removed.v1")]
public sealed record DensityVersionRemoved(Guid MaterialId, string Code, DensityVersion Removed, string? Actor) : DomainEvent;

/// <summary>
/// A raw rock or a product of the company (US-P1-AST-04): its code, name, kind and its density versions. Tenant-owned
/// (RLS), the same for every site. Archived, never deleted: past quantities keep the version valid on their date.
/// </summary>
public sealed class Material : AggregateRoot<MaterialId>, ITenantOwned
{
    private IReadOnlyList<DensityVersion> versions = [];

    private Material(MaterialId id, TenantId tenantId, string code, string name, MaterialKind kind, DensityVersion first, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        Code = code;
        Name = name;
        Kind = kind;
        versions = [first];
        Status = MaterialStatus.Active;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private Material()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    /// <summary>Short code, unique in the company for ever (archived materials keep theirs), e.g. "G-8/16".</summary>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public MaterialKind Kind { get; private set; }

    /// <summary>Oldest first; never empty.</summary>
    public IReadOnlyList<DensityVersion> Versions
    {
        get => versions;
        private set => versions = [.. value.OrderBy(v => v.EffectiveFrom)];
    }

    public MaterialStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public static Material Create(TimeProvider clock, TenantId tenant, string code, string name, MaterialKind kind, DensityVersion first, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(first);
        var at = clock.GetUtcNow();
        var material = new Material(EntityId.New<MaterialId>(clock), tenant, code, name, kind, first, at);
        material.Raise(new MaterialCreated(material.Id.Value, material.Snapshot(), first, actor) { OccurredAt = at });
        return material;
    }

    /// <summary>The version valid on <paramref name="date"/>: the last one starting on or before it, or null before the first.</summary>
    public DensityVersion? VersionOn(DateOnly date) => versions.LastOrDefault(v => v.EffectiveFrom <= date);

    /// <summary>New name and kind; false when nothing changed (no ledger entry). The code never changes.</summary>
    public bool Change(TimeProvider clock, string name, MaterialKind kind, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var before = Snapshot();
        if (string.Equals(name, Name, StringComparison.Ordinal) && kind == Kind)
        {
            return false;
        }

        Name = name;
        Kind = kind;
        ChangedAt = clock.GetUtcNow();
        Raise(new MaterialChanged(Id.Value, before, Snapshot(), actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>
    /// Sets the version starting on its date, which must be after <paramref name="today"/> (the company's clock): a
    /// quantity already converted keeps its densities. A version not yet in effect is replaced; false when unchanged.
    /// </summary>
    public Result<bool> SetVersion(TimeProvider clock, DensityVersion version, DateOnly today, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(version);
        if (Status == MaterialStatus.Archived)
        {
            return MaterialRules.Archived;
        }

        if (version.EffectiveFrom <= today)
        {
            return MaterialRules.DateInPast(today);
        }

        var before = versions.SingleOrDefault(v => v.EffectiveFrom == version.EffectiveFrom);
        if (before == version)
        {
            return false;
        }

        Versions = [.. versions.Where(v => v.EffectiveFrom != version.EffectiveFrom), version];
        ChangedAt = clock.GetUtcNow();
        Raise(new DensityVersionSet(Id.Value, Code, before, version, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Removes a version not yet in effect; the material keeps at least one.</summary>
    public Result<bool> RemoveVersion(TimeProvider clock, DateOnly effectiveFrom, DateOnly today, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == MaterialStatus.Archived)
        {
            return MaterialRules.Archived;
        }

        if (versions.SingleOrDefault(v => v.EffectiveFrom == effectiveFrom) is not { } removed)
        {
            return MaterialRules.VersionNotFound;
        }

        if (effectiveFrom <= today)
        {
            return MaterialRules.DateInPast(today);
        }

        if (versions.Count == 1)
        {
            return MaterialRules.LastVersion;
        }

        Versions = [.. versions.Where(v => v != removed)];
        ChangedAt = clock.GetUtcNow();
        Raise(new DensityVersionRemoved(Id.Value, Code, removed, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Takes the material out of use; false when it already is.</summary>
    public bool Archive(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == MaterialStatus.Archived)
        {
            return false;
        }

        Status = MaterialStatus.Archived;
        ArchivedAt = ChangedAt = clock.GetUtcNow();
        Raise(new MaterialArchived(Id.Value, Code, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Puts an archived material back in use; false when it is active.</summary>
    public bool Restore(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == MaterialStatus.Active)
        {
            return false;
        }

        Status = MaterialStatus.Active;
        ArchivedAt = null;
        ChangedAt = clock.GetUtcNow();
        Raise(new MaterialRestored(Id.Value, Code, actor) { OccurredAt = ChangedAt });
        return true;
    }

    private MaterialSnapshot Snapshot() => new(Code, Name, MaterialRules.KindCode(Kind));
}

/// <summary>
/// Material input rules (US-P1-AST-04 AC1, ADR-0025). Valid ranges are the formula handbook's (§1, draft until the
/// domain expert signs it off): density 1.2–3.2 t/m³, moisture 0–0.25, swell (derived) 0.10–0.80.
/// </summary>
public static partial class MaterialRules
{
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 100;
    public const decimal MinDensity = 1.2m;
    public const decimal MaxDensity = 3.2m;
    public const decimal MaxMoisture = 0.25m;
    public const decimal MinSwell = 0.10m;
    public const decimal MaxSwell = 0.80m;

    /// <summary>Up to 4 decimals: densities are measured to the kilogram per m³ at best.</summary>
    public const int Decimals = 4;

    public static readonly Error CodeInvalid = Error.Validation(
        "materials.code_invalid", "A code is 1 to 20 capital letters, digits, dashes, dots or slashes, starting with a letter or digit.");

    public static readonly Error NameInvalid = Error.Validation("materials.name_invalid", $"A name is 1 to {NameMaxLength} characters.");

    public static readonly Error KindInvalid = Error.Validation("materials.kind_invalid", "The kind is raw-rock or product.");

    public static readonly Error DensityRequired = Error.Validation("materials.density_required", "Give at least one density (bank, loose or compacted).");

    public static readonly Error MoistureInvalid = Error.Validation(
        "materials.moisture_invalid", $"Moisture is a mass fraction from 0 to {MaxMoisture.ToString(CultureInfo.InvariantCulture)} with at most {Decimals} decimals.");

    public static readonly Error Archived = Error.Conflict("materials.material_archived", "An archived material keeps its densities; restore it first.");

    public static readonly Error VersionNotFound = Error.NotFound("materials.density_version_not_found", "No density version starts on this date.");

    public static readonly Error LastVersion = Error.BusinessRule("materials.last_density_version", "A material keeps at least one density version.");

    public static Error DensityInvalid(MaterialState state) =>
        Error.Validation("materials.density_invalid", $"A density is from {MinDensity.ToString(CultureInfo.InvariantCulture)} to {MaxDensity.ToString(CultureInfo.InvariantCulture)} t/m³ with at most {Decimals} decimals.")
            .With("state", MaterialStates.Code(state));

    public static Error SwellInvalid(decimal swell) =>
        Error.Validation("materials.swell_invalid", "Bank and loose densities give a swell factor outside 0.10–0.80; check them.")
            .With("swell", decimal.Round(swell, 4).ToString(CultureInfo.InvariantCulture));

    public static Error DateInPast(DateOnly today) =>
        Error.Validation("materials.date_in_past", "Only densities from tomorrow on can change; past quantities keep theirs.")
            .With("today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public static Result<string> Code(string? code)
    {
        var value = code?.Trim().ToUpperInvariant();
        return value is not null && CodePattern().IsMatch(value) ? value : CodeInvalid;
    }

    public static Result<string> Name(string? name)
    {
        var value = name?.Trim();
        return value is { Length: > 0 and <= NameMaxLength } ? value : NameInvalid;
    }

    public static Result<MaterialKind> Kind(string? kind) => kind switch
    {
        "raw-rock" => MaterialKind.RawRock,
        "product" => MaterialKind.Product,
        _ => KindInvalid,
    };

    public static string KindCode(MaterialKind kind) => kind switch
    {
        MaterialKind.RawRock => "raw-rock",
        MaterialKind.Product => "product",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static Result<DensityVersion> Version(DateOnly effectiveFrom, decimal? bank, decimal? loose, decimal? compacted, decimal? moisture)
    {
        if (bank is null && loose is null && compacted is null)
        {
            return DensityRequired;
        }

        foreach (var (state, density) in new[] { (MaterialState.Bank, bank), (MaterialState.Loose, loose), (MaterialState.Compacted, compacted) })
        {
            if (density is { } value && (value < MinDensity || value > MaxDensity || value.Scale > Decimals))
            {
                return DensityInvalid(state);
            }
        }

        if (moisture is not { } w || w < 0m || w > MaxMoisture || w.Scale > Decimals)
        {
            return MoistureInvalid;
        }

        var version = new DensityVersion(effectiveFrom, Normal(bank), Normal(loose), Normal(compacted), w / 1.000000000000000000000000000000000m);
        return version.Densities.Swell is { } swell && (swell < MinSwell || swell > MaxSwell) ? SwellInvalid(swell) : version;
    }

    // 1.50 and 1.5 are the same density: one representation, so "unchanged" is detected and the ledger is stable.
    private static decimal? Normal(decimal? value) => value / 1.000000000000000000000000000000000m;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9./-]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
