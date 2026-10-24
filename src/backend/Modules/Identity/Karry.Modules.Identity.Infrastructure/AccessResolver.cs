using Karry.BuildingBlocks.Authorization;
using Karry.Modules.Identity.Application;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>
/// The role assignments behind a token (ADR-0022), for <c>PermissionMiddleware</c> on every tenant request. Cached per
/// tenant and subject for <see cref="TimeToLive"/>, like the revocation check: a change of roles reaches the other API
/// instances within 30 s. Scoped: queries with the request's DbContext under the tenant just resolved (row-level
/// security). A subject with no active Karry user has no assignment.
/// </summary>
internal sealed class AccessResolver(IMemoryCache cache, IdentityDbContext db, AuthorizationCatalog catalog) : IAccessResolver
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);

    public async ValueTask<IReadOnlyList<AccessAssignment>> AssignmentsAsync(TenantId tenant, string subject, CancellationToken cancellationToken)
    {
        var key = $"identity:access:{tenant}:{subject}";
        if (cache.TryGetValue(key, out IReadOnlyList<AccessAssignment>? assignments) && assignments is not null)
        {
            return assignments;
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Subject == subject, cancellationToken).ConfigureAwait(false);
        assignments = user is null ? [] : ResolvedRoles.Of(catalog, user);
        cache.Set(key, assignments, TimeToLive);
        return assignments;
    }
}
