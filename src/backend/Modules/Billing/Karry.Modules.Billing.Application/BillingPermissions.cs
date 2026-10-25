using Karry.SharedKernel.Authorization;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Billing.Application;

/// <summary>Billing permissions of tenant users (ADR-0022). Operators manage plans and subscriptions with their own policy.</summary>
public static class BillingPermissions
{
    /// <summary>See the company's plan, state, usage and invoices ("Plan &amp; usage", US-P1-BIL-04 AC3).</summary>
    public const string SubscriptionRead = "billing.subscription.read";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(SubscriptionRead, ScopeKind.Tenant, privileged: false, Roles.TenantAdmin, Roles.Executive, Roles.Finance),
    ];
}
