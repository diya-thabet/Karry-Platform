using System.Globalization;
using Karry.BuildingBlocks.Sync;
using Karry.Modules.Organization.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Organization.Infrastructure;

/// <summary>The tablet's site as the field app keeps it (<c>@karry/offline</c> <c>SiteRow</c>).</summary>
public sealed record SiteSyncRow(Guid Id, string Code, string Name, string Type, string TimeZone);

public sealed record ShiftSyncRow(string Code, string Name, string Start, string End, IReadOnlyList<string> Days);

/// <summary>One version of the site's shift pattern (<c>ShiftPatternRow</c>).</summary>
public sealed record ShiftPatternSyncRow(Guid Id, Guid SiteId, string EffectiveFrom, IReadOnlyList<ShiftSyncRow> Shifts);

/// <summary>
/// The tablet's site and its shift pattern versions (ADR-0028 §5). Both are tiny and a future version can be deleted,
/// so they are always sent whole and replace what the tablet holds; an archived site is sent as none.
/// </summary>
internal sealed class OrganizationSync(OrganizationDbContext db) : ISyncSource
{
    public async Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var siteId = new SiteId(scope.SiteId);
        var site = await db.Sites.AsNoTracking()
            .Where(s => s.Id == siteId && s.Status == SiteStatus.Active)
            .Select(s => new { s.Id, s.Code, s.Name, s.Type, s.TimeZone })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var patterns = site is null
            ? []
            : await db.ShiftPatterns.AsNoTracking().Where(p => p.SiteId == siteId).OrderBy(p => p.EffectiveFrom).ToListAsync(cancellationToken).ConfigureAwait(false);

        object[] sites = site is null ? [] : [new SiteSyncRow(site.Id.Value, site.Code, site.Name, SiteRules.TypeCode(site.Type), site.TimeZone)];
        return
        [
            new SyncDelta("sites", sites, [], Replace: true),
            new SyncDelta("shiftPatterns", [.. patterns.Select(Row)], [], Replace: true),
        ];
    }

    private static ShiftPatternSyncRow Row(ShiftPattern pattern) => new(
        pattern.Id.Value,
        pattern.SiteId.Value,
        pattern.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        [.. pattern.Shifts.Select(s => new ShiftSyncRow(
            s.Code, s.Name, ShiftRules.TimeText(s.Start), ShiftRules.TimeText(s.End),
            [.. s.Days.Select(ShiftRules.DayCode)]))]);
}
