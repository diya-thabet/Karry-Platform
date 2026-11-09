using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Tenancy.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Tenancy.Application;

/// <summary>Tenancy permissions of tenant users (ADR-0022). Every member reads the settings through <c>/current</c>.</summary>
public static class TenancyPermissions
{
    /// <summary>Change the company's settings and logo (US-P1-TEN-04).</summary>
    public const string SettingsManage = "tenancy.settings.manage";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(SettingsManage, ScopeKind.Tenant, privileged: true, Roles.TenantAdmin),
    ];
}

/// <summary>New settings, every field given (a full replacement).</summary>
public sealed record UpdateTenantSettings(
    string? LegalName, string? PrimaryColour, string? Language, string? Currency, string? UnitSystem, string? TimeZone, int FiscalYearStartMonth);

/// <summary>Persistence port of the settings: the catalog row is changed in the tenant's own unit of work (its ledger).</summary>
public interface ITenantSettingsStore
{
    Task<Tenant?> FindAsync(TenantId tenant, CancellationToken cancellationToken);

    Task<TenantLogo?> LogoAsync(TenantId tenant, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the tenant with its events in one transaction; <paramref name="logo"/> replaces the stored logo, or removes
    /// it when <paramref name="removeLogo"/>. False, with nothing saved, when the tenant changed since it was loaded
    /// (its <see cref="Tenant.Version"/>).
    /// </summary>
    Task<bool> TrySaveAsync(Tenant tenant, TenantLogo? logo, bool removeLogo, CancellationToken cancellationToken);
}

/// <summary>US-P1-TEN-04: settings and logo changes, each audited in the company's ledger and visible at once.</summary>
public sealed class TenantSettingsOperations(ITenantSettingsStore store, TimeProvider clock)
{
    /// <summary>
    /// New settings. With <paramref name="expectedVersion"/> (the form's <c>If-Match</c>), only the version the person
    /// edited is changed; otherwise <see cref="RecordVersions.Conflict"/> and nothing is saved (H-C1).
    /// </summary>
    public async Task<Result<Tenant>> UpdateAsync(
        TenantId tenant, UpdateTenantSettings request, string? actor, CancellationToken cancellationToken, uint? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = TenantSettings.Create(request.LegalName, request.PrimaryColour, request.Language, request.Currency, request.UnitSystem,
            request.TimeZone, request.FiscalYearStartMonth);
        if (!settings.IsSuccess)
        {
            return settings.Error;
        }

        var current = await store.FindAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return NotFound;
        }

        if (!RecordVersions.Matches(current.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        return !current.ChangeSettings(settings.Value, actor, clock.GetUtcNow())
            || await store.TrySaveAsync(current, logo: null, removeLogo: false, cancellationToken).ConfigureAwait(false)
            ? current
            : RecordVersions.Conflict;
    }

    public async Task<Result<Tenant>> SetLogoAsync(TenantId tenant, ReadOnlyMemory<byte> content, string? actor, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var logo = TenantLogo.Create(tenant.Value, content.Span, now);
        if (!logo.IsSuccess)
        {
            return logo.Error;
        }

        var current = await store.FindAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return NotFound;
        }

        return !current.ChangeLogo(logo.Value, actor, now) || await store.TrySaveAsync(current, logo.Value, removeLogo: false, cancellationToken).ConfigureAwait(false)
            ? current
            : RecordVersions.Conflict;
    }

    public async Task<Result<Tenant>> RemoveLogoAsync(TenantId tenant, string? actor, CancellationToken cancellationToken)
    {
        var current = await store.FindAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return NotFound;
        }

        return !current.ChangeLogo(null, actor, clock.GetUtcNow()) || await store.TrySaveAsync(current, logo: null, removeLogo: true, cancellationToken).ConfigureAwait(false)
            ? current
            : RecordVersions.Conflict;
    }

    private static Error NotFound => Error.NotFound("tenant.not_found", "The company of this account does not exist.");
}
