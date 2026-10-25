using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Billing.Application;

/// <summary>The entitlement cache: a change on this instance forgets the tenant's entry at once (others within 60 s).</summary>
public interface IEntitlementCache
{
    void Forget(TenantId tenant);
}
