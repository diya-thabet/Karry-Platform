using System.Collections.Concurrent;
using Karry.BuildingBlocks.Authorization;
using Karry.Modules.Identity.Application;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>
/// The role assignments behind a token (ADR-0022), for <c>PermissionMiddleware</c> on every tenant request. Cached per
/// tenant and subject for <see cref="TimeToLive"/>, like the revocation check: a change of roles reaches the other API
/// instances within 30 s; on this instance a role change forgets the whole tenant at once (<see cref="IAccessCache"/>).
/// Scoped: queries with the request's DbContext under the tenant just resolved (row-level security). A subject with no
/// active Karry user has no assignment.
/// </summary>
internal sealed class AccessResolver(IMemoryCache cache, AccessCacheGenerations generations, IdentityDbContext db, RoleDirectory directory)
    : IAccessResolver, IAccessCache
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);

    public async ValueTask<IReadOnlyList<AccessAssignment>> AssignmentsAsync(TenantId tenant, string subject, CancellationToken cancellationToken)
    {
        var key = $"identity:access:{tenant}:{generations.Of(tenant)}:{subject}";
        if (cache.TryGetValue(key, out IReadOnlyList<AccessAssignment>? assignments) && assignments is not null)
        {
            return assignments;
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Subject == subject, cancellationToken).ConfigureAwait(false);
        assignments = user is null ? [] : (await directory.LoadAsync(cancellationToken).ConfigureAwait(false)).Of(user);
        cache.Set(key, assignments, TimeToLive);
        return assignments;
    }

    public void ForgetTenant(TenantId tenant) => generations.Next(tenant);
}

/// <summary>Per-tenant generation in the access cache keys: a new generation makes every older entry unreachable (they expire on their own).</summary>
internal sealed class AccessCacheGenerations
{
    private readonly ConcurrentDictionary<TenantId, long> generations = new();

    public long Of(TenantId tenant) => generations.GetValueOrDefault(tenant);

    public void Next(TenantId tenant) => generations.AddOrUpdate(tenant, 1, (_, generation) => generation + 1);
}
