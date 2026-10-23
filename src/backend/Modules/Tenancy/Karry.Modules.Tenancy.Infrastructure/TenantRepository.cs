using Karry.Modules.Tenancy.Application;
using Karry.Modules.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Tenancy.Infrastructure;

internal sealed class TenantRepository(TenancyDbContext db) : ITenantRepository
{
    public Task<Tenant?> FindBySlugAsync(TenantSlug slug, CancellationToken cancellationToken) =>
        db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken);

    public async Task<bool> TryAddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        db.Tenants.Add(tenant);
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
