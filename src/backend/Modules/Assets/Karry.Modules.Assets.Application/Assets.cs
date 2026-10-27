using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Domain;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using NodaTime;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Assets.Application;

/// <summary>
/// Assets permissions (ADR-0022, ADR-0026). Every role sees the machines of its sites; the people who run a site or plan
/// its maintenance register them. Site-scoped: a site role reaches only the assets on its sites.
/// </summary>
public static class AssetsPermissions
{
    /// <summary>See the assets (a site role: those on its sites), the type catalogue, and resolve labels.</summary>
    public const string RegistryRead = "assets.registry.read";

    /// <summary>Register, change, move and set the status of assets on the caller's sites (US-P1-AST-02).</summary>
    public const string RegistryManage = "assets.registry.manage";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(RegistryRead, ScopeKind.Site, privileged: false,
            Roles.TenantAdmin, Roles.Executive, Roles.Finance, Roles.Controller, Roles.SiteManager, Roles.MaintenancePlanner, Roles.Commercial,
            Roles.Storekeeper, Roles.Weighmaster, Roles.FuelAttendant, Roles.Mechanic, Roles.Operator),
        new(RegistryManage, ScopeKind.Site, privileged: false, Roles.TenantAdmin, Roles.SiteManager, Roles.MaintenancePlanner),
    ];
}

/// <summary>A new asset on a site; it starts <c>active</c>.</summary>
public sealed record AssetInput(
    string? Code, string? Name, string? Type, string? Make, string? Model, string? SerialNumber, string? Plate, int? Year, DateOnly? AcquiredOn,
    Guid? SiteId);

/// <summary>New details of an asset (the code never changes; the site and status have their own operations).</summary>
public sealed record AssetChangeInput(
    string? Name, string? Type, string? Make, string? Model, string? SerialNumber, string? Plate, int? Year, DateOnly? AcquiredOn);

/// <summary>
/// A search of the register (US-P1-AST-02 AC4): <c>Q</c> matches a fragment of the code, name, make, model, serial or
/// plate; filters by site, type and status (disposed assets only when that status is asked). Ordered by code; the next
/// page starts after <c>Cursor</c> (the last code of the previous one).
/// </summary>
public sealed record AssetSearch(string? Q, Guid? SiteId, string? Type, string? Status, string? Cursor, int? Limit);

/// <summary>A search as the store runs it: <c>Sites</c> null means every site of the company.</summary>
public sealed record AssetQuery(string? Text, Guid? SiteId, string? Type, AssetStatus? Status, IReadOnlyCollection<Guid>? Sites, string? After, int Take);

public sealed record AssetPage(IReadOnlyList<Asset> Items, string? NextCursor);

/// <summary>Persistence port of assets (the caller's unit of work, RLS).</summary>
public interface IAssetStore
{
    Task<Asset?> FindAsync(AssetId id, CancellationToken cancellationToken);

    /// <summary>Up to <c>Take</c> assets matching the query, ordered by code.</summary>
    Task<IReadOnlyList<Asset>> SearchAsync(AssetQuery query, CancellationToken cancellationToken);

    /// <summary>Saves with its events; false when the code is already used in the company (unique index).</summary>
    Task<bool> TrySaveAsync(Asset asset, CancellationToken cancellationToken);
}

/// <summary>
/// US-P1-AST-02: the company's machines, each change in the company's ledger. Registering an asset, or bringing a
/// disposed one back, follows the asset limit (soft limit). A site role reads and manages only the assets on its sites.
/// </summary>
public sealed class AssetOperations(
    IAssetStore store, ISiteDirectory sites, ITenantDirectory tenants, ILimitGuard limits, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static readonly Error NotFound = Error.NotFound("assets.asset_not_found", "No such asset on your sites.");

    public static readonly Error CodeTaken = Error.Conflict("assets.asset_code_taken", "Another asset of the company already has this code.");

    public static readonly Error SiteInvalid = Error.Validation("assets.site_invalid", "Choose an active site you manage.");

    public static readonly Error SiteForbidden = Error.Forbidden("assets.site_forbidden", "You do not manage the site this asset is on.");

    public static readonly Error LimitInvalid = Error.Validation("assets.limit_invalid", $"A page holds 1 to {MaxLimit} assets.");

    public async Task<Result<AssetPage>> SearchAsync(AssetSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var take = search.Limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return LimitInvalid;
        }

        if (search.Type is not null && AssetTypes.Find(search.Type) is null)
        {
            return AssetRules.TypeInvalid;
        }

        AssetStatus? status = null;
        if (search.Status is not null)
        {
            var parsed = AssetRules.Status(search.Status);
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            status = parsed.Value;
        }

        var scope = caller.Current.ScopeOf(AssetsPermissions.RegistryRead);
        var text = search.Q?.Trim();
        var query = new AssetQuery(
            string.IsNullOrEmpty(text) ? null : text.ToLowerInvariant(), search.SiteId, search.Type, status,
            scope.TenantWide ? null : scope.Sites, search.Cursor, take + 1);
        var found = await store.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return found.Count > take ? new AssetPage([.. found.Take(take)], found[take - 1].Code) : new AssetPage(found, null);
    }

    /// <summary>An asset the caller may see; outside their sites it does not exist for them (404).</summary>
    public async Task<Result<Asset>> GetAsync(AssetId id, CancellationToken cancellationToken)
    {
        var asset = await store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return asset is not null && caller.Current.ScopeOf(AssetsPermissions.RegistryRead).CoversSite(asset.SiteId) ? asset : NotFound;
    }

    public async Task<Result<Asset>> RegisterAsync(AssetInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var code = AssetRules.Code(input.Code);
        if (!code.IsSuccess)
        {
            return code.Error;
        }

        var details = AssetRules.Details(
            input.Name, input.Type, input.Make, input.Model, input.SerialNumber, input.Plate, input.Year, input.AcquiredOn,
            await TodayAsync(cancellationToken).ConfigureAwait(false));
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        if (input.SiteId is not { } siteId || !await ManagedActiveSiteAsync(siteId, cancellationToken).ConfigureAwait(false))
        {
            return SiteInvalid;
        }

        if (await limits.CheckCreateAsync(Metrics.Assets, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var asset = Asset.Register(clock, Tenant, code.Value, details.Value, siteId, caller.Subject);
        return await store.TrySaveAsync(asset, cancellationToken).ConfigureAwait(false) ? asset : CodeTaken;
    }

    public async Task<Result<Asset>> ChangeAsync(AssetId id, AssetChangeInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var asset = await ManagedAsync(id, cancellationToken).ConfigureAwait(false);
        if (!asset.IsSuccess)
        {
            return asset;
        }

        var details = AssetRules.Details(
            input.Name, input.Type, input.Make, input.Model, input.SerialNumber, input.Plate, input.Year, input.AcquiredOn,
            await TodayAsync(cancellationToken).ConfigureAwait(false));
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        return await SaveAsync(asset.Value, asset.Value.Change(clock, details.Value, caller.Subject), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves an asset to another active site; the caller manages both.</summary>
    public async Task<Result<Asset>> MoveAsync(AssetId id, Guid? siteId, CancellationToken cancellationToken)
    {
        var asset = await ManagedAsync(id, cancellationToken).ConfigureAwait(false);
        if (!asset.IsSuccess)
        {
            return asset;
        }

        if (siteId is not { } to || (to != asset.Value.SiteId && !await ManagedActiveSiteAsync(to, cancellationToken).ConfigureAwait(false)))
        {
            return SiteInvalid;
        }

        return await SaveAsync(asset.Value, asset.Value.MoveTo(clock, to, caller.Subject), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Asset>> SetStatusAsync(AssetId id, string? status, CancellationToken cancellationToken)
    {
        var asset = await ManagedAsync(id, cancellationToken).ConfigureAwait(false);
        if (!asset.IsSuccess)
        {
            return asset;
        }

        var parsed = AssetRules.Status(status);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        if (asset.Value.Status == AssetStatus.Disposed && parsed.Value != AssetStatus.Disposed
            && await limits.CheckCreateAsync(Metrics.Assets, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        return await SaveAsync(asset.Value, asset.Value.SetStatus(clock, parsed.Value, caller.Subject), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Today on the company's clock (its settings' time zone).</summary>
    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        var zone = (await tenants.FindAsync(Tenant, cancellationToken).ConfigureAwait(false))?.TimeZone ?? "UTC";
        var date = Instant.FromDateTimeOffset(clock.GetUtcNow()).InZone(DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone) ?? DateTimeZone.Utc).Date;
        return new DateOnly(date.Year, date.Month, date.Day);
    }

    /// <summary>An asset the caller may change: 404 when they cannot see it, 403 when they see it but do not manage its site.</summary>
    private async Task<Result<Asset>> ManagedAsync(AssetId id, CancellationToken cancellationToken)
    {
        var asset = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!asset.IsSuccess)
        {
            return asset;
        }

        return caller.Current.ScopeOf(AssetsPermissions.RegistryManage).CoversSite(asset.Value.SiteId) ? asset : SiteForbidden;
    }

    private async Task<bool> ManagedActiveSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        caller.Current.ScopeOf(AssetsPermissions.RegistryManage).CoversSite(siteId)
        && await sites.FindAsync(siteId, cancellationToken).ConfigureAwait(false) is { Archived: false };

    private async Task<Result<Asset>> SaveAsync(Asset asset, Result<bool> changed, CancellationToken cancellationToken)
    {
        if (!changed.IsSuccess)
        {
            return changed.Error;
        }

        if (changed.Value)
        {
            await store.TrySaveAsync(asset, cancellationToken).ConfigureAwait(false);
        }

        return asset;
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Assets need a tenant.");
}
