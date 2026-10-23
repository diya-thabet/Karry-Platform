using Karry.SharedKernel.Tenancy;

namespace Karry.BuildingBlocks.Tenancy;

/// <summary>
/// Resolves the first label of a request address (<c>acme</c> in <c>acme.karry.app</c>). Implemented by the Tenancy
/// module; used by <see cref="TenantResolutionMiddleware"/> to check that a token is used on its own tenant's address.
/// </summary>
public interface ITenantSlugLookup
{
    Task<SlugResolution> ResolveAsync(string label, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IsTenantAddress"/> false: a platform address (<c>api</c>, <c>www</c>, invalid label), no check applies.
/// True with a null <see cref="Tenant"/>: a well-formed address of no company, refused.
/// </summary>
public readonly record struct SlugResolution(bool IsTenantAddress, TenantId? Tenant)
{
    public static SlugResolution NotATenantAddress => new(false, null);

    public static SlugResolution UnknownTenant => new(true, null);

    public static SlugResolution Of(TenantId tenant) => new(true, tenant);
}
