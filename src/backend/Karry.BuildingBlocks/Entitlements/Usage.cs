using Karry.SharedKernel.Results;

namespace Karry.BuildingBlocks.Entitlements;

/// <summary>
/// A module's count of one metric (<see cref="SharedKernel.Entitlements.Metrics"/>) for the current tenant, read in the
/// caller's unit of work (RLS). Registered as <c>IUsageSource</c> by the module that owns the counted thing.
/// </summary>
public interface IUsageSource
{
    string Metric { get; }

    Task<long> CountAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The soft-limit check a creating handler calls before it adds a counted item (ADR-0023): null when allowed, else
/// 403 <c>billing.limit_reached</c> (members <c>metric</c>, <c>limit</c>, <c>usage</c>). Implemented by Billing with
/// the live count. Field capture never calls it.
/// </summary>
public interface ILimitGuard
{
    /// <summary>Whether <paramref name="count"/> more items may be created (an import creates many at once).</summary>
    Task<Error?> CheckCreateAsync(string metric, int count, CancellationToken cancellationToken);
}

public static class LimitGuardExtensions
{
    /// <summary>Whether one more item may be created.</summary>
    public static Task<Error?> CheckCreateAsync(this ILimitGuard guard, string metric, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return guard.CheckCreateAsync(metric, 1, cancellationToken);
    }
}
