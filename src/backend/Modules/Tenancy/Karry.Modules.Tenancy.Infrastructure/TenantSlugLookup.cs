using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Tenancy.Infrastructure;

/// <summary>
/// Slug → tenant for address checks, cached for one minute (slugs never change; a new tenant's address works at once
/// because misses are not cached).
/// </summary>
internal sealed class TenantSlugLookup(IServiceScopeFactory scopes, IMemoryCache cache) : ITenantSlugLookup
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    public async Task<SlugResolution> ResolveAsync(string label, CancellationToken cancellationToken)
    {
        var valid = TenantSlug.Create(label);
        if (!valid.IsSuccess)
        {
            return SlugResolution.NotATenantAddress; // reserved platform name or not a slug at all
        }

        var key = $"tenancy:slug:{valid.Value.Value}";
        if (cache.TryGetValue(key, out TenantId cached))
        {
            return SlugResolution.Of(cached);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Slug == valid.Value).Select(t => (TenantId?)t.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (tenant is not { } found)
        {
            return SlugResolution.UnknownTenant;
        }

        cache.Set(key, found, Lifetime);
        return SlugResolution.Of(found);
    }
}
