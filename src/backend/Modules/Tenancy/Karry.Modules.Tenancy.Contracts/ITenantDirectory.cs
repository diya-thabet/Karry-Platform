using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Tenancy.Contracts;

/// <summary>The list of tenants, for platform jobs that work tenant by tenant (ledger anchoring, usage snapshots).</summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<TenantId>> ListAsync(CancellationToken cancellationToken);
}
