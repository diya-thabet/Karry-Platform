using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Materials.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Karry.SharedKernel.Units;
using NodaTime;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Materials.Application;

/// <summary>
/// Materials permissions (ADR-0022). Everyone who records or prices quantities reads the catalogue; changing a density
/// changes invoiced volumes, so it is privileged (step-up) and company-wide.
/// </summary>
public static class MaterialsPermissions
{
    /// <summary>See the materials and convert quantities with their densities.</summary>
    public const string CatalogRead = "materials.catalog.read";

    /// <summary>Create, change, archive and restore materials and set their density versions (US-P1-AST-04).</summary>
    public const string CatalogManage = "materials.catalog.manage";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(CatalogRead, ScopeKind.Tenant, privileged: false,
            Roles.TenantAdmin, Roles.Executive, Roles.Finance, Roles.Controller, Roles.SiteManager, Roles.MaintenancePlanner, Roles.Commercial,
            Roles.Storekeeper, Roles.Weighmaster, Roles.FuelAttendant, Roles.Mechanic, Roles.Operator),
        new(CatalogManage, ScopeKind.Tenant, privileged: true, Roles.TenantAdmin),
    ];
}

/// <summary>Densities as the API receives them (t/m³; moisture as a mass fraction).</summary>
public sealed record DensityInput(decimal? Bank, decimal? Loose, decimal? Compacted, decimal? Moisture);

/// <summary>A new material with its first densities; <c>EffectiveFrom</c> defaults to the company's today (any date is allowed).</summary>
public sealed record MaterialInput(string? Code, string? Name, string? Kind, DensityInput? Densities, DateOnly? EffectiveFrom);

public sealed record MaterialChangeInput(string? Name, string? Kind);

/// <summary>A quantity as the API receives it: <c>{value, unit, state}</c>.</summary>
public sealed record QuantityInput(decimal? Value, string? Unit, string? State);

/// <summary>
/// Converts <c>Quantity</c> to <c>Unit</c> (and <c>State</c> for m³) with the densities valid on <c>On</c> (default: the
/// company's today); <c>Moisture</c> replaces the material's default (a measured sample).
/// </summary>
public sealed record ConversionInput(QuantityInput? Quantity, string? Unit, string? State, DateOnly? On, decimal? Moisture);

/// <summary>A conversion and the density version it used.</summary>
public sealed record MaterialConversionResult(Conversion Conversion, DensityVersion Version);

/// <summary>Persistence port of materials (the caller's unit of work, RLS).</summary>
public interface IMaterialStore
{
    Task<Material?> FindAsync(MaterialId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Material>> ListAsync(bool includeArchived, CancellationToken cancellationToken);

    /// <summary>Saves with its events; false when the code is already used in the company (unique index).</summary>
    Task<bool> TrySaveAsync(Material material, CancellationToken cancellationToken);
}

/// <summary>
/// US-P1-AST-04 and US-P1-UNI-01: the company's materials with their density versions, each change in the company's
/// ledger, and conversions with the version valid on a date (TC-P1-UNI-002). "Today" is on the company's clock.
/// </summary>
public sealed class MaterialOperations(IMaterialStore store, ITenantDirectory tenants, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("materials.material_not_found", "No such material in this company.");

    public static readonly Error CodeTaken = Error.Conflict("materials.material_code_taken", "Another material of the company already has this code.");

    public static readonly Error QuantityInvalid = Error.Validation(
        "units.quantity_invalid", "A quantity is {value, unit, state} with a known unit, and a state (bank, loose, compacted) exactly when the unit is m3.");

    public static Error NoVersion(DateOnly on) =>
        Error.Validation("materials.no_density_version", "The material has no densities on this date.")
            .With("on", on.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    public async Task<IReadOnlyList<Material>> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        [.. (await store.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false)).OrderBy(m => m.Code, StringComparer.Ordinal)];

    public async Task<Result<Material>> GetAsync(MaterialId id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } material ? material : NotFound;

    public async Task<Result<Material>> CreateAsync(MaterialInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var code = MaterialRules.Code(input.Code);
        if (!code.IsSuccess)
        {
            return code.Error;
        }

        var name = MaterialRules.Name(input.Name);
        if (!name.IsSuccess)
        {
            return name.Error;
        }

        var kind = MaterialRules.Kind(input.Kind);
        if (!kind.IsSuccess)
        {
            return kind.Error;
        }

        var from = input.EffectiveFrom ?? await TodayAsync(cancellationToken).ConfigureAwait(false);
        var version = Version(from, input.Densities);
        if (!version.IsSuccess)
        {
            return version.Error;
        }

        var material = Material.Create(clock, Tenant, code.Value, name.Value, kind.Value, version.Value, caller.Subject);
        return await store.TrySaveAsync(material, cancellationToken).ConfigureAwait(false) ? material : CodeTaken;
    }

    public async Task<Result<Material>> ChangeAsync(MaterialId id, MaterialChangeInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var material = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!material.IsSuccess)
        {
            return material;
        }

        var name = MaterialRules.Name(input.Name);
        if (!name.IsSuccess)
        {
            return name.Error;
        }

        var kind = MaterialRules.Kind(input.Kind);
        if (!kind.IsSuccess)
        {
            return kind.Error;
        }

        if (material.Value.Change(clock, name.Value, kind.Value, caller.Subject))
        {
            await store.TrySaveAsync(material.Value, cancellationToken).ConfigureAwait(false);
        }

        return material;
    }

    public async Task<Result<Material>> ArchiveAsync(MaterialId id, CancellationToken cancellationToken)
    {
        var material = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (material.IsSuccess && material.Value.Archive(clock, caller.Subject))
        {
            await store.TrySaveAsync(material.Value, cancellationToken).ConfigureAwait(false);
        }

        return material;
    }

    public async Task<Result<Material>> RestoreAsync(MaterialId id, CancellationToken cancellationToken)
    {
        var material = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (material.IsSuccess && material.Value.Restore(clock, caller.Subject))
        {
            await store.TrySaveAsync(material.Value, cancellationToken).ConfigureAwait(false);
        }

        return material;
    }

    /// <summary>Sets the version starting on <paramref name="effectiveFrom"/>, a date after the company's today.</summary>
    public async Task<Result<DensityVersion>> SetVersionAsync(MaterialId id, DateOnly effectiveFrom, DensityInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var material = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!material.IsSuccess)
        {
            return material.Error;
        }

        var version = Version(effectiveFrom, input);
        if (!version.IsSuccess)
        {
            return version.Error;
        }

        var set = material.Value.SetVersion(clock, version.Value, await TodayAsync(cancellationToken).ConfigureAwait(false), caller.Subject);
        if (!set.IsSuccess)
        {
            return set.Error;
        }

        if (set.Value)
        {
            await store.TrySaveAsync(material.Value, cancellationToken).ConfigureAwait(false);
        }

        return version.Value;
    }

    public async Task<Result<bool>> RemoveVersionAsync(MaterialId id, DateOnly effectiveFrom, CancellationToken cancellationToken)
    {
        var material = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!material.IsSuccess)
        {
            return material.Error;
        }

        var removed = material.Value.RemoveVersion(clock, effectiveFrom, await TodayAsync(cancellationToken).ConfigureAwait(false), caller.Subject);
        if (removed.IsSuccess)
        {
            await store.TrySaveAsync(material.Value, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>US-P1-UNI-01 AC2 and US-P1-AST-04 AC2: converts with the version valid on the given date (also for an archived material).</summary>
    public async Task<Result<MaterialConversionResult>> ConvertAsync(MaterialId id, ConversionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var material = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!material.IsSuccess)
        {
            return material.Error;
        }

        if (Parse(input.Quantity) is not { } quantity || Unit.Find(input.Unit) is not { } unit || !StateMatches(unit, input.State, out var state))
        {
            return QuantityInvalid;
        }

        if (input.Moisture is { } moisture && (moisture < 0m || moisture > MaterialRules.MaxMoisture))
        {
            return MaterialRules.MoistureInvalid;
        }

        var on = input.On ?? await TodayAsync(cancellationToken).ConfigureAwait(false);
        if (material.Value.VersionOn(on) is not { } version)
        {
            return NoVersion(on);
        }

        var conversion = MaterialConversion.Convert(quantity, unit, state, version.Densities, input.Moisture);
        return conversion.IsSuccess ? new MaterialConversionResult(conversion.Value, version) : conversion.Error;
    }

    /// <summary>Today on the company's clock (its settings' time zone).</summary>
    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        var zone = (await tenants.FindAsync(Tenant, cancellationToken).ConfigureAwait(false))?.TimeZone ?? "UTC";
        var date = Instant.FromDateTimeOffset(clock.GetUtcNow()).InZone(DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone) ?? DateTimeZone.Utc).Date;
        return new DateOnly(date.Year, date.Month, date.Day);
    }

    private static Result<DensityVersion> Version(DateOnly effectiveFrom, DensityInput? input) =>
        input is null ? MaterialRules.DensityRequired : MaterialRules.Version(effectiveFrom, input.Bank, input.Loose, input.Compacted, input.Moisture);

    private static Quantity? Parse(QuantityInput? input)
    {
        if (input?.Value is not { } value || Unit.Find(input.Unit) is not { } unit || !StateMatches(unit, input.State, out var state))
        {
            return null;
        }

        return new Quantity(value, unit, state);
    }

    private static bool StateMatches(Unit unit, string? code, out MaterialState? state)
    {
        state = MaterialStates.Find(code);
        return unit.HasState ? state is not null : code is null;
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Materials need a tenant.");
}
