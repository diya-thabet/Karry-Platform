using Karry.BuildingBlocks.Authorization;
using Karry.Modules.FieldOps.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.FieldOps.Application;

/// <summary>
/// FieldOps permissions (ADR-0022, ADR-0034 §7). Site-scoped: a site role reads the entries of its sites only. The
/// people who run or check a site's work read its shift entries; field roles see their own history on the tablet (P2.5).
/// </summary>
public static class FieldOpsPermissions
{
    /// <summary>Read the shift entries of the caller's sites, with every version (ADR-0034 §7).</summary>
    public const string EntriesRead = "fieldops.entries.read";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(EntriesRead, ScopeKind.Site, privileged: false,
            Roles.TenantAdmin, Roles.Executive, Roles.Finance, Roles.Controller, Roles.SiteManager, Roles.MaintenancePlanner),
    ];
}

/// <summary>
/// A search of the shift entries (ADR-0034 §7): by site, shift (date and code), machine, operator, status
/// (<c>open</c>, <c>closed</c>) and whether the current version is flagged. Newest first; <c>Cursor</c> is the last
/// entry ID of the previous page.
/// </summary>
public sealed record EntrySearch(
    Guid? SiteId = null, DateOnly? ShiftDate = null, string? ShiftCode = null, Guid? AssetId = null, Guid? OperatorId = null,
    string? Status = null, bool? Flagged = null, Guid? Cursor = null, int? Limit = null);

/// <summary>A checked search for the reader; <c>Sites</c> null means every site of the company.</summary>
public sealed record EntryQuery(
    IReadOnlySet<Guid>? Sites, Guid? SiteId, DateOnly? ShiftDate, string? ShiftCode, Guid? AssetId, Guid? OperatorId,
    ShiftEntryStatus? Status, bool? Flagged, Guid? After, int Take);

/// <summary>A page of entries (their current versions), newest first.</summary>
public sealed record EntryPage(IReadOnlyList<ShiftEntryVersion> Items, Guid? NextCursor);

/// <summary>Reads shift entries for the portal (RLS: the current company only).</summary>
public interface IShiftEntryReader
{
    /// <summary>Current versions (the highest of each entry) matching the query, newest entry first (UUIDv7 order).</summary>
    Task<IReadOnlyList<ShiftEntryVersion>> SearchAsync(EntryQuery query, CancellationToken cancellationToken);

    /// <summary>Every version of an entry, oldest first; empty when the entry is unknown.</summary>
    Task<IReadOnlyList<ShiftEntryVersion>> VersionsAsync(Guid rootId, CancellationToken cancellationToken);
}

/// <summary>The portal's reading of shift entries (ADR-0034 §7), limited to the caller's sites (ADR-0022).</summary>
public sealed class EntryReading(IShiftEntryReader reader, ICallerAccess caller)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static readonly Error NotFound = Error.NotFound("fieldops.entry_not_found", "No such shift entry on your sites.");

    public static readonly Error LimitInvalid = Error.Validation("fieldops.limit_invalid", $"A page holds 1 to {MaxLimit} entries.");

    /// <summary>The status as the API writes it.</summary>
    public static string StatusCode(ShiftEntryStatus status) => status == ShiftEntryStatus.Open ? "open" : "closed";

    private static readonly Dictionary<string, ShiftEntryStatus> StatusCodes = new(StringComparer.Ordinal)
    {
        ["open"] = ShiftEntryStatus.Open,
        ["closed"] = ShiftEntryStatus.Closed,
    };

    public static readonly Error StatusInvalid = Error.Validation("fieldops.status_invalid", "The status is open or closed.");

    public async Task<Result<EntryPage>> SearchAsync(EntrySearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var take = search.Limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return LimitInvalid;
        }

        ShiftEntryStatus? status = null;
        if (search.Status is not null)
        {
            if (!StatusCodes.TryGetValue(search.Status, out var parsed))
            {
                return StatusInvalid;
            }

            status = parsed;
        }

        var scope = caller.Current.ScopeOf(FieldOpsPermissions.EntriesRead);
        var found = await reader.SearchAsync(
            new EntryQuery(scope.TenantWide ? null : scope.Sites, search.SiteId, search.ShiftDate, search.ShiftCode, search.AssetId, search.OperatorId,
                status, search.Flagged, search.Cursor, take + 1),
            cancellationToken).ConfigureAwait(false);
        return found.Count > take ? new EntryPage([.. found.Take(take)], found[take - 1].RootId) : new EntryPage(found, null);
    }

    /// <summary>An entry with all its versions, oldest first; outside the caller's sites it does not exist for them (404).</summary>
    public async Task<Result<IReadOnlyList<ShiftEntryVersion>>> GetAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var versions = await reader.VersionsAsync(rootId, cancellationToken).ConfigureAwait(false);
        return versions.Count > 0 && caller.Current.ScopeOf(FieldOpsPermissions.EntriesRead).CoversSite(versions[^1].SiteId)
            ? Result.Success(versions)
            : NotFound;
    }
}
