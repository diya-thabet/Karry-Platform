using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Trust.Application;

/// <summary>
/// Gives every ledger entry its subject row for the audit viewer's filters (ADR-0029 §1): entries written before the
/// index existed, or whose mapping changed. Run by <c>Karry.Host migrate</c>; idempotent.
/// </summary>
public interface ILedgerSubjectBackfill
{
    /// <summary>Fills the missing subject rows of these tenants; returns how many were added.</summary>
    Task<int> RunAsync(IEnumerable<TenantId> tenants, CancellationToken cancellationToken);
}
