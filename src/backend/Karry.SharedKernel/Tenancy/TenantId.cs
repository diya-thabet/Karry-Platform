using Karry.SharedKernel.Identity;

namespace Karry.SharedKernel.Tenancy;

/// <summary>Identifier of a customer company (tenant). Every tenant-owned row carries it (ADR-0004).</summary>
public readonly record struct TenantId(Guid Value) : IEntityId<TenantId>
{
    public static TenantId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// An entity that belongs to exactly one tenant. Its table gets <c>tenant_id</c> with row-level security, and the
/// module DbContext filters it by the current tenant (two independent layers).
/// </summary>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}

/// <summary>
/// An aggregate whose events belong in a tenant's audit ledger although the aggregate is not a tenant-owned row
/// (the tenant catalog entry itself). <see cref="ITenantOwned"/> aggregates are audited under their tenant automatically.
/// </summary>
public interface ITenantAudited
{
    TenantId AuditTenantId { get; }
}
