using Karry.Modules.Tenancy.Domain;

namespace Karry.Modules.Tenancy.Application;

/// <summary>Persistence port of the tenant catalog.</summary>
public interface ITenantRepository
{
    Task<Tenant?> FindBySlugAsync(TenantSlug slug, CancellationToken cancellationToken);

    /// <summary>Every tenant, by name (the operator console).</summary>
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Saves a new tenant with its events; false when the slug was taken concurrently (unique index).</summary>
    Task<bool> TryAddAsync(Tenant tenant, CancellationToken cancellationToken);
}
