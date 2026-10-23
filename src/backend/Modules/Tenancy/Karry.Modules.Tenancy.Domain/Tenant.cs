using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Tenancy.Domain;

public enum TenantStatus
{
    /// <summary>Provisioned and usable. Trial, restricted and suspended states arrive with subscriptions (P1.10).</summary>
    Active = 1,
}

/// <summary>Raised once when a tenant is provisioned; other modules create their defaults from it (P1.8–P1.11).</summary>
[EventType("tenancy.tenant_provisioned.v1")]
public sealed record TenantProvisioned(Guid TenantId, string Slug, string Name) : DomainEvent;

/// <summary>
/// A customer company in the tenant catalog (US-P1-TEN-01). The catalog row itself is platform data, not tenant-owned:
/// it is read to resolve addresses and by operators, and holds no business data of the tenant.
/// </summary>
public sealed class Tenant : AggregateRoot<TenantId>, ITenantAudited
{
    public const int NameMaxLength = 120;

    private Tenant(TenantId id, TenantSlug slug, string name, DateTimeOffset createdAt)
        : base(id)
    {
        Slug = slug;
        Name = name;
        CreatedAt = createdAt;
        Status = TenantStatus.Active;
    }

    public TenantSlug Slug { get; private set; }

    public string Name { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The tenant's own ledger starts with its provisioning.</summary>
    TenantId ITenantAudited.AuditTenantId => Id;

    public static Result<Tenant> Provision(TimeProvider clock, string? slug, string? name)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var validSlug = TenantSlug.Create(slug);
        if (!validSlug.IsSuccess)
        {
            return validSlug.Error;
        }

        var validName = NormalizeName(name);
        if (!validName.IsSuccess)
        {
            return validName.Error;
        }

        var tenant = new Tenant(EntityId.New<TenantId>(clock), validSlug.Value, validName.Value, clock.GetUtcNow());
        tenant.Raise(new TenantProvisioned(tenant.Id.Value, tenant.Slug.Value, tenant.Name) { OccurredAt = tenant.CreatedAt });
        return tenant;
    }

    /// <summary>Trimmed, inner whitespace collapsed, 2–120 characters, no control characters.</summary>
    public static Result<string> NormalizeName(string? name)
    {
        var value = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (value.Length is < 2 or > NameMaxLength || value.Any(char.IsControl))
        {
            return Error.Validation("tenant.name_invalid", $"The company name must be 2–{NameMaxLength} characters.");
        }

        return value;
    }
}
