using Karry.Modules.Tenancy.Application;
using Karry.Modules.Tenancy.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Tenancy.Infrastructure;

internal sealed class TenantSettingsStore(TenancyDbContext db) : ITenantSettingsStore
{
    public Task<Tenant?> FindAsync(TenantId tenant, CancellationToken cancellationToken) =>
        db.Tenants.SingleOrDefaultAsync(t => t.Id == tenant, cancellationToken);

    public Task<TenantLogo?> LogoAsync(TenantId tenant, CancellationToken cancellationToken) =>
        db.Logos.AsNoTracking().SingleOrDefaultAsync(l => l.TenantId == tenant.Value, cancellationToken);

    public async Task<bool> TrySaveAsync(Tenant tenant, TenantLogo? logo, bool removeLogo, CancellationToken cancellationToken)
    {
        // The old logo is deleted by its own command: without one transaction around it and the tenant's save, a refused
        // save (or any failure) left the company with its logo gone while the settings still pointed at it.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (logo is not null || removeLogo)
            {
                await db.Logos.Where(l => l.TenantId == tenant.Id.Value).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            if (logo is not null)
            {
                db.Logos.Add(logo);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A version check failed, or another logo was inserted meanwhile (its insert runs before the tenant's
            // update): either way someone else changed the company first.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
