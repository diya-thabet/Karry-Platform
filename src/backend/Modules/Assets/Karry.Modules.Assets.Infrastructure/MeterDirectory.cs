using Karry.Modules.Assets.Contracts;
using Karry.Modules.Assets.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Assets.Infrastructure;

/// <summary>Meters for other modules (ADR-0034): the meter, its machine and the machine's site history.</summary>
internal sealed class MeterDirectory(AssetsDbContext db) : IMeterDirectory
{
    public async Task<MeterSummary?> FindAsync(Guid meterId, CancellationToken cancellationToken)
    {
        var found = await db.Meters.AsNoTracking()
            .Where(m => m.Id == new MeterId(meterId))
            .Join(db.Assets.AsNoTracking(), m => m.AssetId, a => a.Id, (m, a) => new { Meter = m, Asset = a })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        return new MeterSummary(
            meterId, found.Asset.Id.Value, found.Asset.Code, AssetRules.MeterKindCode(found.Meter.Kind), MeterRules.StatusCode(found.Meter.Status),
            [.. found.Asset.Sites.OrderBy(s => s.From).Select(s => new AssetSiteAssignment(s.SiteId, s.From))]);
    }
}
