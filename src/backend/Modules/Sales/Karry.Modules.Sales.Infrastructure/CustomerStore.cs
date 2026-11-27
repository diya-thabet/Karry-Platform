using Karry.Modules.Sales.Application;
using Karry.Modules.Sales.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Sales.Infrastructure;

internal sealed class CustomerStore(SalesDbContext db) : ICustomerStore
{
    public Task<Customer?> FindAsync(CustomerId id, CancellationToken cancellationToken) =>
        db.Customers.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Customer>> ListAsync(CustomerStatus? status, string? search, CancellationToken cancellationToken)
    {
        var query = db.Customers.AsNoTracking();
        if (status is { } wanted)
        {
            query = query.Where(c => c.Status == wanted);
        }

        if (!string.IsNullOrEmpty(search))
        {
            // The weighmaster types part of a code, a name or a tax ID (case-insensitive).
            var pattern = $"%{search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            query = query.Where(c => EF.Functions.ILike(c.Code, pattern) || EF.Functions.ILike(c.LegalName, pattern) ||
                                     (c.TaxId != null && EF.Functions.ILike(c.TaxId, pattern)));
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Customer>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        await db.Customers.Where(c => codes.Contains(c.Code)).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(Customer customer) => db.Customers.Add(customer);

    public Task<bool> TrySaveAllAsync(CancellationToken cancellationToken) => SalesSaving.TrySaveAsync(db, cancellationToken);

    public async Task<bool> TrySaveAsync(Customer customer, CancellationToken cancellationToken)
    {
        if (db.Entry(customer).State == EntityState.Detached)
        {
            db.Customers.Add(customer);
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
}
