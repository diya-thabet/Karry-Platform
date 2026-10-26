using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Tenancy.Contracts;

/// <summary>
/// A tenant as other modules see it: its address label, display name and IANA time zone (the company's clock, for
/// "today" when nothing more local applies, e.g. a material's density version).
/// </summary>
public sealed record TenantSummary(TenantId Id, string Slug, string Name, string TimeZone = "UTC");

/// <summary>The list of tenants, for platform jobs that work tenant by tenant (ledger anchoring, usage snapshots).</summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<TenantId>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One tenant (invitation links and messages need its address and name), or null.</summary>
    Task<TenantSummary?> FindAsync(TenantId tenant, CancellationToken cancellationToken);
}

/// <summary>
/// Reacts to a newly provisioned tenant (US-P1-TEN-01 AC1): other modules create their defaults (Identity: Keycloak
/// organization and app addresses; later roles, subscription, settings). Called by the Tenancy outbox dispatcher, at
/// least once per tenant: implementations must be idempotent.
/// </summary>
public interface ITenantProvisionedHandler
{
    Task HandleAsync(TenantSummary tenant, CancellationToken cancellationToken);
}
