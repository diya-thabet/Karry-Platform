using Karry.BuildingBlocks.Sync;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.FieldOps.Infrastructure;

/// <summary>The newest published version of an asset type's checklist, as the tablet answers it (ADR-0035 §6).</summary>
public sealed record ChecklistTemplateSyncRow(Guid Id, string AssetType, int Version, bool Required, IReadOnlyList<ChecklistItem> Items);

/// <summary>An open hold (ADR-0035 §4): the tablet refuses a start on <see cref="AssetId"/> while it is listed.</summary>
public sealed record SafetyHoldSyncRow(Guid Id, Guid AssetId, IReadOnlyList<string> Items, DateTimeOffset OpenedAt);

/// <summary>
/// The <c>checklistTemplates</c> and <c>safetyHolds</c> collections of the tablet's pull (ADR-0035 §6), both whole at
/// every pull (<c>replace</c>): a company has a few checklists and fewer open holds, and a released hold must vanish.
/// Templates: the newest version of each asset type (company-wide, as checklists are). Holds: every open hold of the
/// company, so a machine moved to another site keeps its hold on the new site's tablets.
/// </summary>
internal sealed class ChecklistsSync(FieldOpsDbContext db) : ISyncSource
{
    public const string Templates = "checklistTemplates";

    public const string Holds = "safetyHolds";

    public async Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var templates = await db.ChecklistTemplates.AsNoTracking()
            .Where(c => !db.ChecklistTemplates.Any(newer => newer.AssetType == c.AssetType && newer.Version > c.Version))
            .OrderBy(c => c.AssetType)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var holds = await db.SafetyHolds.AsNoTracking()
            .Where(h => !db.SafetyHoldReleases.Any(r => r.Id == h.Id))
            .OrderBy(h => h.OpenedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            new SyncDelta(Templates, [.. templates.Select(c => new ChecklistTemplateSyncRow(c.Id.Value, c.AssetType, c.Version, c.Required, c.Items))], [], Replace: true),
            new SyncDelta(Holds, [.. holds.Select(h => new SafetyHoldSyncRow(h.Id.Value, h.AssetId, h.Items, h.OpenedAt))], [], Replace: true),
        ];
    }
}
