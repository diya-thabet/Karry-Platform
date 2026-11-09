using Karry.BuildingBlocks.Entitlements;
using Karry.Modules.Assets.Application;
using Karry.Modules.Assets.Domain;
using Karry.SharedKernel.Entitlements;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Assets.Infrastructure;

internal sealed class AssetStore(AssetsDbContext db) : IAssetStore
{
    public Task<Asset?> FindAsync(AssetId id, CancellationToken cancellationToken) =>
        db.Assets.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<Asset?> FindByLabelAsync(string token, CancellationToken cancellationToken) =>
        db.Assets.SingleOrDefaultAsync(a => a.LabelToken == token, cancellationToken);

    public async Task<IReadOnlyList<Asset>> SearchAsync(AssetQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var assets = db.Assets.AsNoTracking();
        assets = query.Status is { } status ? assets.Where(a => a.Status == status) : assets.Where(a => a.Status != AssetStatus.Disposed);
        if (query.Sites is { } sites)
        {
            assets = assets.Where(a => sites.Contains(a.SiteId));
        }

        if (query.SiteId is { } siteId)
        {
            assets = assets.Where(a => a.SiteId == siteId);
        }

        if (query.Type is { } type)
        {
            assets = assets.Where(a => a.Type == type);
        }

        if (query.Text is { } text)
        {
            var pattern = $"%{text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal)}%";
            assets = assets.Where(a => EF.Functions.Like(EF.Property<string>(a, AssetsDbContext.SearchText), pattern, @"\"));
        }

        if (query.After is { } after)
        {
            assets = assets.Where(a => string.Compare(a.Code, after) > 0);
        }

        return await assets.OrderBy(a => a.Code).Take(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TrySaveAsync(Asset asset, CancellationToken cancellationToken)
    {
        if (db.Entry(asset).State == EntityState.Detached)
        {
            db.Assets.Add(asset);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<Asset>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        await db.Assets.Where(a => codes.Contains(a.Code)).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(Asset asset) => db.Assets.Add(asset);

    public async Task<bool> TrySaveAllAsync(CancellationToken cancellationToken)
    {
        // One SaveChanges is one transaction: the assets, meters, readings, their outbox rows and ledger entries.
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

/// <summary>The asset meter (US-P1-AST-02 AC3): assets not disposed; disposing one frees its place.</summary>
internal sealed class CountedAssets(AssetsDbContext db) : IUsageSource
{
    public string Metric => Metrics.Assets;

    public async Task<long> CountAsync(CancellationToken cancellationToken) =>
        await db.Assets.LongCountAsync(a => a.Status != AssetStatus.Disposed, cancellationToken).ConfigureAwait(false);
}
