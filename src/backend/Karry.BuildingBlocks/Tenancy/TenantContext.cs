using Karry.SharedKernel.Tenancy;

namespace Karry.BuildingBlocks.Tenancy;

/// <summary>
/// The tenant of the current unit of work (one HTTP request, one outbox message). Scoped. Set once, by the tenant
/// resolution middleware from the validated token, or by the dispatcher from the message; it can never switch to
/// another tenant within the same scope.
/// </summary>
public interface ITenantContext
{
    TenantId? Current { get; }

    void Set(TenantId tenant);
}

public sealed class TenantContext : ITenantContext
{
    public TenantId? Current { get; private set; }

    public void Set(TenantId tenant)
    {
        if (tenant.Value == Guid.Empty)
        {
            throw new ArgumentException("The empty GUID is not a tenant.", nameof(tenant));
        }

        if (Current is { } existing && existing != tenant)
        {
            throw new InvalidOperationException("The tenant of a unit of work cannot change.");
        }

        Current = tenant;
    }
}
