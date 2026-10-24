using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>
/// "Is this account deactivated?" for every tenant request (TC-P1-IAM-007), cached per tenant and subject for
/// <see cref="TimeToLive"/>: another API instance notices a deactivation within 30 s, well inside the 60 s promise;
/// the instance that deactivated forgets its entry at once. Scoped: it queries with the request's DbContext, under the
/// tenant the middleware just set (row-level security).
/// </summary>
internal sealed class RevocationCache(IMemoryCache cache, IdentityDbContext db) : IAccessRevocation, IRevocationCache
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);

    public async ValueTask<bool> IsRevokedAsync(TenantId tenant, string subject, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(Key(tenant, subject), out bool revoked))
        {
            return revoked;
        }

        revoked = await db.Users.AsNoTracking().AnyAsync(u => u.Subject == subject && u.Status == UserStatus.Deactivated, cancellationToken).ConfigureAwait(false);
        cache.Set(Key(tenant, subject), revoked, TimeToLive);
        return revoked;
    }

    public void Forget(TenantId tenant, string subject) => cache.Remove(Key(tenant, subject));

    private static string Key(TenantId tenant, string subject) => $"identity:revoked:{tenant}:{subject}";
}
