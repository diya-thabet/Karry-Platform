using Karry.BuildingBlocks.Sync;
using Karry.Modules.Materials.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Materials.Infrastructure;

/// <summary>A material as the field app keeps it (<c>@karry/offline</c> <c>MaterialRow</c>).</summary>
public sealed record MaterialSyncRow(Guid Id, string Code, string Name, string Kind);

/// <summary>The company's active materials (ADR-0028 §5); an archived one is sent as a remove.</summary>
internal sealed class MaterialsSync(MaterialsDbContext db) : ISyncSource
{
    public async Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var materials = await db.Materials.AsNoTracking()
            .Where(m => since == null ? m.Status == MaterialStatus.Active : m.ChangedAt >= since)
            .OrderBy(m => m.Code)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            new SyncDelta(
                "materials",
                [.. materials.Where(m => m.Status == MaterialStatus.Active).Select(m => new MaterialSyncRow(m.Id.Value, m.Code, m.Name, MaterialRules.KindCode(m.Kind)))],
                [.. materials.Where(m => m.Status != MaterialStatus.Active).Select(m => m.Id.Value)]),
        ];
    }
}
