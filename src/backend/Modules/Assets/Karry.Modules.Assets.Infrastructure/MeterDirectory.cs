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

/// <summary>Machines for other modules (ADR-0035): the machine, its type and its site history.</summary>
internal sealed class AssetDirectory(AssetsDbContext db) : IAssetDirectory
{
    public async Task<AssetSummary?> FindAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == new AssetId(assetId), cancellationToken).ConfigureAwait(false);
        return asset is null
            ? null
            : new AssetSummary(assetId, asset.Code, asset.Type, [.. asset.Sites.OrderBy(s => s.From).Select(s => new AssetSiteAssignment(s.SiteId, s.From))]);
    }
}
