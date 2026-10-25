using Karry.Modules.Flags.Application;
using Karry.Modules.Flags.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Flags.Infrastructure;

internal sealed class FlagStore(FlagsDbContext db) : IFlagStore
{
    public async Task<IReadOnlyList<PlatformFlag>> PlatformAsync(CancellationToken cancellationToken) =>
        await db.PlatformFlags.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<PlatformFlag?> FindPlatformAsync(string key, CancellationToken cancellationToken) =>
        db.PlatformFlags.SingleOrDefaultAsync(f => f.Key == key, cancellationToken);

    public void Add(PlatformFlag flag) => db.PlatformFlags.Add(flag);

    public void Add(PlatformFlagChange change) => db.PlatformFlagChanges.Add(change);

    public async Task<IReadOnlyList<TenantFlag>> TenantAsync(CancellationToken cancellationToken) =>
        await db.TenantFlags.AsNoTracking().OrderBy(f => f.Key).ThenBy(f => f.Subject).ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<TenantFlag?> FindTenantAsync(string key, string subject, CancellationToken cancellationToken) =>
        db.TenantFlags.SingleOrDefaultAsync(f => f.Key == key && f.Subject == subject, cancellationToken);

    public void Add(TenantFlag flag) => db.TenantFlags.Add(flag);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
