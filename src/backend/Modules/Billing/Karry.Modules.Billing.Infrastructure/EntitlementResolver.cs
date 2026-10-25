using System.Collections.Concurrent;
using Karry.BuildingBlocks.Entitlements;
using Karry.Modules.Billing.Application;
using Karry.Modules.Billing.Domain;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Karry.Modules.Billing.Infrastructure;

/// <summary>
/// Resolves entitlements from the tenant's subscription and its plan version, cached ≤ 60 s per tenant (US-P1-BIL-03
/// AC3). Scoped: it reads the subscription in the request's unit of work (the tenant's RLS context).
/// </summary>
internal sealed class EntitlementResolver(BillingDbContext db, IMemoryCache cache, EntitlementCacheGenerations generations) : IEntitlementResolver
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public async ValueTask<TenantEntitlements> ResolveAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        var key = (nameof(EntitlementResolver), tenant, generations.Of(tenant));
        if (cache.TryGetValue(key, out TenantEntitlements? cached) && cached is not null)
        {
            return cached;
        }

        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var plan = subscription is null
            ? await db.Plans.AsNoTracking().Where(p => p.Code == TrialOnProvisioning.TrialPlan).OrderByDescending(p => p.Version)
                .FirstAsync(cancellationToken).ConfigureAwait(false)
            : await db.Plans.AsNoTracking().SingleAsync(p => p.Id == new PlanId(subscription.Terms.PlanId), cancellationToken).ConfigureAwait(false);
        var resolved = EntitlementRules.Resolve(plan, subscription);
        cache.Set(key, resolved, Lifetime);
        return resolved;
    }
}

/// <summary>Per-tenant generation in the cache keys: a new generation makes older entries unreachable (they expire on their own).</summary>
internal sealed class EntitlementCacheGenerations : IEntitlementCache
{
    private readonly ConcurrentDictionary<TenantId, long> generations = new();

    public long Of(TenantId tenant) => generations.GetValueOrDefault(tenant);

    public void Forget(TenantId tenant) => generations.AddOrUpdate(tenant, 1, (_, generation) => generation + 1);
}
