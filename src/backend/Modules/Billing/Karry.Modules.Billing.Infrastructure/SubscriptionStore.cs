using Karry.Modules.Billing.Application;
using Karry.Modules.Billing.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Billing.Infrastructure;

/// <summary>The current tenant's subscription and invoices (the query filter and RLS keep other tenants' rows out).</summary>
internal sealed class SubscriptionStore(BillingDbContext db) : ISubscriptionStore
{
    public Task<Subscription?> FindAsync(CancellationToken cancellationToken) => db.Subscriptions.SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Invoice>> InvoicesAsync(CancellationToken cancellationToken) =>
        await db.Invoices.AsNoTracking().OrderByDescending(i => i.IssuedOn).ThenByDescending(i => i.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<Invoice?> FindInvoiceAsync(InvoiceId id, CancellationToken cancellationToken) =>
        db.Invoices.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);

    public void Add(Subscription subscription) => db.Subscriptions.Add(subscription);

    public void Add(Invoice invoice) => db.Invoices.Add(invoice);

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
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
