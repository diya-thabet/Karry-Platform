using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.FieldOps.Infrastructure;

internal sealed class ShiftEntryReader(FieldOpsDbContext db) : IShiftEntryReader
{
    private static readonly IReadOnlyList<string> NoFlags = [];

    public async Task<IReadOnlyList<ShiftEntryVersion>> SearchAsync(EntryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = db.ShiftEntries.AsNoTracking().Where(e => !db.ShiftEntries.Any(later => later.RootId == e.RootId && later.Version > e.Version));
        if (query.Sites is { } sites)
        {
            entries = entries.Where(e => sites.Contains(e.SiteId));
        }

        if (query.SiteId is { } site)
        {
            entries = entries.Where(e => e.SiteId == site);
        }

        if (query.ShiftDate is { } date)
        {
            entries = entries.Where(e => e.ShiftDate == date);
        }

        if (query.ShiftCode is { } code)
        {
            entries = entries.Where(e => e.ShiftCode == code);
        }

        if (query.AssetId is { } asset)
        {
            entries = entries.Where(e => e.AssetId == asset);
        }

        if (query.OperatorId is { } person)
        {
            entries = entries.Where(e => e.OperatorId == person);
        }

        if (query.Status is { } status)
        {
            entries = entries.Where(e => e.Status == status);
        }

        if (query.Flagged is { } flagged)
        {
            entries = flagged ? entries.Where(e => e.Flags != NoFlags) : entries.Where(e => e.Flags == NoFlags);
        }

        if (query.After is { } after)
        {
            entries = entries.Where(e => e.RootId.CompareTo(after) < 0);
        }

        return await entries.OrderByDescending(e => e.RootId).Take(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShiftEntryVersion>> VersionsAsync(Guid rootId, CancellationToken cancellationToken) =>
        await db.ShiftEntries.AsNoTracking().Where(e => e.RootId == rootId).OrderBy(e => e.Version).ToListAsync(cancellationToken).ConfigureAwait(false);
}
