using Karry.Modules.Materials.Application;
using Karry.Modules.Materials.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Materials.Infrastructure;

internal sealed class MaterialStore(MaterialsDbContext db) : IMaterialStore
{
    public Task<Material?> FindAsync(MaterialId id, CancellationToken cancellationToken) =>
        db.Materials.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Material>> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        await db.Materials.AsNoTracking().Where(m => includeArchived || m.Status == MaterialStatus.Active).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> TrySaveAsync(Material material, CancellationToken cancellationToken)
    {
        if (db.Entry(material).State == EntityState.Detached)
        {
            db.Materials.Add(material);
        }

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

    public async Task<IReadOnlyList<Material>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        await db.Materials.Where(x => codes.Contains(x.Code)).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(Material material) => db.Materials.Add(material);

    public async Task<bool> TrySaveAllAsync(CancellationToken cancellationToken)
    {
        // One SaveChanges is one transaction: the materials, their outbox rows and ledger entries.
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
