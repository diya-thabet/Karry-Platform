using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Tenancy.Infrastructure;

internal sealed class TenantDirectory(TenancyDbContext db) : ITenantDirectory
{
    public async Task<IReadOnlyList<TenantId>> ListAsync(CancellationToken cancellationToken) =>
        await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<TenantSummary?> FindAsync(TenantId tenant, CancellationToken cancellationToken) =>
        await db.Tenants.AsNoTracking().Where(t => t.Id == tenant)
            .Select(t => new TenantSummary(t.Id, t.Slug.Value, t.Name)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
}
