using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.FieldOps.Infrastructure;

internal sealed class ChecklistTemplateStore(FieldOpsDbContext db) : IChecklistTemplateStore
{
    public async Task<IReadOnlyList<ChecklistTemplate>> LatestAsync(CancellationToken cancellationToken)
    {
        // A few asset types with a few versions each: load them and pick the newest per type in memory.
        var all = await db.ChecklistTemplates.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.GroupBy(c => c.AssetType).Select(g => g.MaxBy(c => c.Version)!).OrderBy(c => c.AssetType, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<ChecklistTemplate>> VersionsAsync(string assetType, CancellationToken cancellationToken) =>
        await db.ChecklistTemplates.AsNoTracking().Where(c => c.AssetType == assetType).OrderByDescending(c => c.Version).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> TryAddAsync(ChecklistTemplate template, CancellationToken cancellationToken)
    {
        db.ChecklistTemplates.Add(template);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
