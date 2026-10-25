using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Billing.Domain;

/// <summary>
/// One day's count of one metric for a tenant (US-P1-BIL-04 AC1), with the limit that applied: the history behind
/// "Plan &amp; usage" and the over-limit notice. Reporting only: limits are checked against the live count. Taken again the
/// same day, the value is replaced (the last run of the day wins).
/// </summary>
public sealed class UsageSnapshot : ITenantOwned
{
    private UsageSnapshot(TenantId tenantId, string metric, DateOnly day)
    {
        TenantId = tenantId;
        Metric = metric;
        Day = day;
    }

    public TenantId TenantId { get; private set; }

    public string Metric { get; private set; }

    /// <summary>The UTC day of the count.</summary>
    public DateOnly Day { get; private set; }

    public long Value { get; private set; }

    /// <summary>The tenant-wide limit on that day; null when unlimited.</summary>
    public long? Limit { get; private set; }

    public DateTimeOffset TakenAt { get; private set; }

    public string State => SoftLimit.State(Value, Limit);

    public static UsageSnapshot Take(TenantId tenant, string metric, DateTimeOffset at, long value, long? limit)
    {
        if (!Metrics.IsKnown(metric))
        {
            throw new ArgumentException($"Unknown metric '{metric}'.", nameof(metric));
        }

        var snapshot = new UsageSnapshot(tenant, metric, DateOnly.FromDateTime(at.UtcDateTime));
        snapshot.Update(at, value, limit);
        return snapshot;
    }

    public void Update(DateTimeOffset at, long value, long? limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
        Limit = limit;
        TakenAt = at;
    }
}
