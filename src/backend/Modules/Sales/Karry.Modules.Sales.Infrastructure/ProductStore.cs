using Karry.Modules.Sales.Application;
using Karry.Modules.Sales.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Sales.Infrastructure;

internal sealed class ProductStore(SalesDbContext db) : IProductStore
{
    public Task<Product?> FindAsync(ProductId id, CancellationToken cancellationToken) =>
        db.Products.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Product>> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        await db.Products.AsNoTracking().Where(p => includeArchived || p.Status == ProductStatus.Active).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<Product>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        await db.Products.Where(p => codes.Contains(p.Code)).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(Product product) => db.Products.Add(product);

    public Task<bool> TrySaveAllAsync(CancellationToken cancellationToken) => SalesSaving.TrySaveAsync(db, cancellationToken);

    public Task<bool> TrySaveAsync(Product product, CancellationToken cancellationToken)
    {
        if (db.Entry(product).State == EntityState.Detached)
        {
            db.Products.Add(product);
        }

        return SalesSaving.TrySaveAsync(db, cancellationToken);
    }
}

internal sealed class PriceListStore(SalesDbContext db) : IPriceListStore
{
    public Task<PriceList?> FindAsync(PriceListId id, CancellationToken cancellationToken) =>
        db.PriceLists.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PriceList>> ListAsync(Guid? siteId, Guid? customerId, bool siteOnly, CancellationToken cancellationToken)
    {
        var query = db.PriceLists.AsNoTracking();
        if (siteId is { } site)
        {
            query = query.Where(l => l.SiteId == site);
        }

        if (siteOnly)
        {
            query = query.Where(l => l.CustomerId == null);
        }
        else if (customerId is { } customer)
        {
            query = query.Where(l => l.CustomerId == customer);
        }

        return await query.OrderByDescending(l => l.EffectiveFrom).ThenBy(l => l.SiteId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PriceList>> ActiveForAsync(Guid siteId, Guid? customerId, CancellationToken cancellationToken) =>
        await db.PriceLists.AsNoTracking()
            .Where(l => l.SiteId == siteId && l.Status == PriceListStatus.Active && (l.CustomerId == null || l.CustomerId == customerId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> TrySaveAsync(PriceList list, CancellationToken cancellationToken)
    {
        if (db.Entry(list).State == EntityState.Detached)
        {
            db.PriceLists.Add(list);
        }

        return SalesSaving.TrySaveAsync(db, cancellationToken);
    }
}

internal static class SalesSaving
{
    /// <summary>One SaveChanges, one transaction (rows, outbox, ledger); false on a unique or version conflict, with nothing kept tracked.</summary>
    public static async Task<bool> TrySaveAsync(SalesDbContext db, CancellationToken cancellationToken)
    {
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
