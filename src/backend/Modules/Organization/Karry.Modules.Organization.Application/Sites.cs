using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Organization.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Organization.Application;

/// <summary>Organization permissions (ADR-0022). Sites are read by everyone who works on them; changing them costs money (the plan is per site).</summary>
public static class OrganizationPermissions
{
    /// <summary>See the sites (a site role: only its sites).</summary>
    public const string SitesRead = "organization.sites.read";

    /// <summary>Create, change, archive and restore sites (US-P1-ORG-01).</summary>
    public const string SitesManage = "organization.sites.manage";

    /// <summary>Set a site's shift patterns and non-working days (US-P1-ORG-02); a site manager on their sites.</summary>
    public const string ShiftsManage = "organization.shifts.manage";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(SitesRead, ScopeKind.Site, privileged: false,
            Roles.TenantAdmin, Roles.Executive, Roles.Finance, Roles.Controller, Roles.SiteManager, Roles.MaintenancePlanner, Roles.Commercial,
            Roles.Storekeeper, Roles.Weighmaster, Roles.FuelAttendant, Roles.Mechanic, Roles.Operator),
        new(SitesManage, ScopeKind.Tenant, privileged: true, Roles.TenantAdmin),
        new(ShiftsManage, ScopeKind.Site, privileged: false, Roles.TenantAdmin, Roles.SiteManager),
    ];
}

/// <summary>Site input as the API receives it; <c>Boundary</c> is a GeoJSON polygon's coordinates ([longitude, latitude]).</summary>
public sealed record SiteInput(
    string? Code, string? Name, string? Type, AddressInput? Address, LocationInput? Location,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>? Boundary, string? TimeZone, string? Currency)
{
    public Result<SiteDetails> Validate() =>
        SiteRules.Validate(Code, Name, Type, Address?.Line, Address?.City, Address?.Region, Address?.PostalCode, Address?.Country,
            Location?.Latitude ?? double.NaN, Location?.Longitude ?? double.NaN, Boundary, TimeZone, Currency);
}

public sealed record AddressInput(string? Line, string? City, string? Region, string? PostalCode, string? Country);

public sealed record LocationInput(double Latitude, double Longitude);

/// <summary>Persistence port of sites (the caller's unit of work, RLS).</summary>
public interface ISiteStore
{
    Task<Site?> FindAsync(SiteId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Site>> ListAsync(bool includeArchived, CancellationToken cancellationToken);

    /// <summary>
    /// Saves with its events; false, with nothing saved, when the code is already used in the company (unique index) or
    /// the site changed since it was loaded (its <see cref="Site.Version"/>).
    /// </summary>
    Task<bool> TrySaveAsync(Site site, CancellationToken cancellationToken);

    /// <summary>The sites with these codes (tracked, for an import), archived ones included.</summary>
    Task<IReadOnlyList<Site>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Tracks a new site, saved with the next save of the unit of work.</summary>
    void Add(Site site);

    /// <summary>Saves everything tracked in one transaction (an import); false, with nothing saved, when a code was taken meanwhile.</summary>
    Task<bool> TrySaveAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// US-P1-ORG-01: sites, each change in the company's ledger; creating or restoring one follows the site limit (soft
/// limit). A change of the site's clock, an archive or a restore updates its stored shift instances in the same transaction.
/// </summary>
public sealed class SiteOperations(ISiteStore store, ShiftInstances shifts, ILimitGuard limits, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("organization.site_not_found", "No such site in this company.");

    public static readonly Error CodeTaken = Error.Conflict("organization.site_code_taken", "Another site of the company already has this code.");

    /// <summary>The sites the caller may see; archived ones on request.</summary>
    public async Task<IReadOnlyList<Site>> ListAsync(bool includeArchived, CancellationToken cancellationToken)
    {
        var scope = caller.Current.ScopeOf(OrganizationPermissions.SitesRead);
        var sites = await store.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false);
        return [.. sites.Where(s => scope.CoversSite(s.Id.Value)).OrderBy(s => s.Code, StringComparer.Ordinal)];
    }

    /// <summary>A site the caller may see; outside their scope it does not exist for them (404).</summary>
    public async Task<Result<Site>> GetAsync(SiteId id, CancellationToken cancellationToken)
    {
        var site = await store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return site is not null && caller.Current.ScopeOf(OrganizationPermissions.SitesRead).CoversSite(id.Value) ? site : NotFound;
    }

    public async Task<Result<Site>> CreateAsync(SiteInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var details = input.Validate();
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        if (await limits.CheckCreateAsync(Metrics.Sites, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var site = Site.Create(clock, Tenant, details.Value, caller.Subject);
        return await store.TrySaveAsync(site, cancellationToken).ConfigureAwait(false) ? site : CodeTaken;
    }

    /// <summary>
    /// New details for a site. With <paramref name="expectedVersion"/> (the form's <c>If-Match</c>), only the version
    /// the person edited is changed; otherwise <see cref="RecordVersions.Conflict"/> and nothing is saved (H-C1).
    /// </summary>
    public async Task<Result<Site>> ChangeAsync(SiteId id, SiteInput input, CancellationToken cancellationToken, uint? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var site = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!site.IsSuccess)
        {
            return site;
        }

        if (!RecordVersions.Matches(site.Value.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        var details = input.Validate();
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        var changed = site.Value.Change(clock, details.Value, caller.Subject);
        if (!changed.IsSuccess)
        {
            return changed.Error;
        }

        if (changed.Value)
        {
            await shifts.ReconcileAsync(site.Value, cancellationToken).ConfigureAwait(false);
            if (!await store.TrySaveAsync(site.Value, cancellationToken).ConfigureAwait(false))
            {
                return RecordVersions.Conflict; // the code never changes: only a concurrent change refuses this save
            }
        }

        return site;
    }

    public async Task<Result<Site>> ArchiveAsync(SiteId id, CancellationToken cancellationToken)
    {
        var site = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (site.IsSuccess && site.Value.Archive(clock, caller.Subject))
        {
            await shifts.ReconcileAsync(site.Value, cancellationToken).ConfigureAwait(false);
            if (!await store.TrySaveAsync(site.Value, cancellationToken).ConfigureAwait(false))
            {
                return RecordVersions.Conflict;
            }
        }

        return site;
    }

    public async Task<Result<Site>> RestoreAsync(SiteId id, CancellationToken cancellationToken)
    {
        var site = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!site.IsSuccess || site.Value.Status == SiteStatus.Active)
        {
            return site;
        }

        if (await limits.CheckCreateAsync(Metrics.Sites, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        site.Value.Restore(clock, caller.Subject);
        await shifts.ReconcileAsync(site.Value, cancellationToken).ConfigureAwait(false);
        return await store.TrySaveAsync(site.Value, cancellationToken).ConfigureAwait(false) ? site : RecordVersions.Conflict;
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Sites need a tenant.");
}
