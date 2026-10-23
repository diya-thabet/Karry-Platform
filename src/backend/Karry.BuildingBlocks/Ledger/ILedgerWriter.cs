using System.Data.Common;
using Karry.SharedKernel.Tenancy;

namespace Karry.BuildingBlocks.Ledger;

/// <summary>A domain event to record in a tenant's audit ledger (US-P1-AUD-01).</summary>
public sealed record LedgerRecord(TenantId Tenant, Guid EventId, string EventType, string Payload, DateTimeOffset OccurredAt);

/// <summary>
/// Appends ledger entries <b>inside the caller's transaction</b> (ADR-0008): <see cref="Persistence.ModuleDbContext"/>
/// calls it after saving the business change and outbox rows, before committing, so all three commit or roll back
/// together. Implemented by the Trust module.
/// </summary>
public interface ILedgerWriter
{
    Task AppendAsync(DbConnection connection, DbTransaction transaction, TenantId? unitOfWorkTenant,
        IReadOnlyList<LedgerRecord> records, CancellationToken cancellationToken);
}
