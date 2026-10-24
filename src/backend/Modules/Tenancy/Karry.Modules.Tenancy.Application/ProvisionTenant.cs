using Karry.Modules.Tenancy.Domain;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Tenancy.Application;

/// <summary>A tenant to provision; <c>Admin</c> (optional) is invited by e-mail as first Tenant Admin (US-P1-TEN-01 AC1).</summary>
public sealed record ProvisionTenant(string Slug, string Name, FirstAdmin? Admin = null);

public sealed record FirstAdmin(string FirstName, string LastName, string Email);

public sealed record ProvisionedTenant(Guid Id, string Slug, string Name, DateTimeOffset CreatedAt, bool Created);

/// <summary>
/// Provisions a tenant (US-P1-TEN-01 AC1–AC2). <b>Idempotent by slug</b>: provisioning the same slug with the same
/// name again returns the existing tenant (<c>Created = false</c>), also when two operators race; the same slug with a
/// different name is a conflict. The catalog row and the <c>tenancy.tenant_provisioned.v1</c> event commit together;
/// identity, settings and subscription defaults are created by subscribers in P1.8–P1.11.
/// </summary>
public sealed class ProvisionTenantHandler(ITenantRepository tenants, TimeProvider clock)
{
    public async Task<Result<ProvisionedTenant>> HandleAsync(ProvisionTenant command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tenant = Tenant.Provision(clock, command.Slug, command.Name);
        if (!tenant.IsSuccess)
        {
            return tenant.Error;
        }

        var existing = await tenants.FindBySlugAsync(tenant.Value.Slug, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return SameRequest(existing, tenant.Value.Name);
        }

        if (await tenants.TryAddAsync(tenant.Value, cancellationToken).ConfigureAwait(false))
        {
            return Summary(tenant.Value, created: true);
        }

        // Lost a race on the unique slug: the winner's row decides, exactly as if it had existed before.
        var winner = await tenants.FindBySlugAsync(tenant.Value.Slug, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Slug '{tenant.Value.Slug}' reported taken but not found.");
        return SameRequest(winner, tenant.Value.Name);
    }

    private static Result<ProvisionedTenant> SameRequest(Tenant existing, string requestedName) =>
        string.Equals(existing.Name, requestedName, StringComparison.Ordinal)
            ? Summary(existing, created: false)
            : Error.Conflict("tenant.slug_taken", $"The address '{existing.Slug}' already belongs to another company.");

    private static ProvisionedTenant Summary(Tenant tenant, bool created) =>
        new(tenant.Id.Value, tenant.Slug.Value, tenant.Name, tenant.CreatedAt, created);
}
