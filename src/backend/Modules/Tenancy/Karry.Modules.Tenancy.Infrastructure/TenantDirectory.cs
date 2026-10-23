using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Tenancy.Infrastructure;

internal sealed class TenantDirectory(TenancyDbContext db) : ITenantDirectory
{
    public async Task<IReadOnlyList<TenantId>> ListAsync(CancellationToken cancellationToken) =>
        await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
}
