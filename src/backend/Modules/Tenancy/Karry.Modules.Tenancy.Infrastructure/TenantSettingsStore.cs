using Karry.Modules.Tenancy.Application;
using Karry.Modules.Tenancy.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Tenancy.Infrastructure;

internal sealed class TenantSettingsStore(TenancyDbContext db) : ITenantSettingsStore
{
    public Task<Tenant?> FindAsync(TenantId tenant, CancellationToken cancellationToken) =>
        db.Tenants.SingleOrDefaultAsync(t => t.Id == tenant, cancellationToken);

    public Task<TenantLogo?> LogoAsync(TenantId tenant, CancellationToken cancellationToken) =>
        db.Logos.AsNoTracking().SingleOrDefaultAsync(l => l.TenantId == tenant.Value, cancellationToken);

    public async Task SaveAsync(Tenant tenant, TenantLogo? logo, bool removeLogo, CancellationToken cancellationToken)
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
    }
}
