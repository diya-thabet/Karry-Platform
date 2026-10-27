using System.Diagnostics.Metrics;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Billing.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Billing.Application;

/// <summary>Usage snapshots of the current tenant (tenant-owned, row-level security).</summary>
public interface IUsageStore
{
    Task<IReadOnlyList<UsageSnapshot>> OnDayAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary>The day of the newest snapshot, if any.</summary>
    Task<DateOnly?> LatestDayAsync(CancellationToken cancellationToken);

    void Add(UsageSnapshot snapshot);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>One metric now: the live count, the tenant-wide limit (null: unlimited), its ceiling and the soft-limit state.</summary>
public sealed record UsageLine(string Metric, long Usage, long? Limit, long? Ceiling, string State);

/// <summary>
/// Live usage of the current tenant against its entitlements (US-P1-BIL-04 AC3). Only metrics a module reports are
/// listed (sites and assets appear when their modules ship (P1.11, P1.13)).
/// </summary>
public sealed class UsageMeter(IEnumerable<IUsageSource> sources, IEntitlementResolver resolver, ITenantContext tenant)
{
    public async Task<IReadOnlyList<UsageLine>> CurrentAsync(CancellationToken cancellationToken)
    {
        var entitlements = await EntitlementsAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<UsageLine>();
        foreach (var source in sources.OrderBy(s => Metrics.All.ToList().IndexOf(s.Metric)))
        {
            lines.Add(Line(source.Metric, await source.CountAsync(cancellationToken).ConfigureAwait(false), entitlements));
        }

        return lines;
    }

    /// <summary>One metric now; throws when no module reports it (a handler guarding a metric nobody counts is a bug).</summary>
    public async Task<UsageLine> CurrentAsync(string metric, CancellationToken cancellationToken)
    {
        var source = sources.SingleOrDefault(s => s.Metric == metric)
            ?? throw new InvalidOperationException($"No usage source counts '{metric}'.");
        var entitlements = await EntitlementsAsync(cancellationToken).ConfigureAwait(false);
        return Line(metric, await source.CountAsync(cancellationToken).ConfigureAwait(false), entitlements);
    }

    private ValueTask<TenantEntitlements> EntitlementsAsync(CancellationToken cancellationToken) =>
        resolver.ResolveAsync(tenant.Current ?? throw new InvalidOperationException("Usage needs a tenant."), cancellationToken);

    private static UsageLine Line(string metric, long usage, TenantEntitlements entitlements)
    {
        var limit = entitlements.MetricLimit(metric);
        return new UsageLine(metric, usage, limit, limit is { } l ? SoftLimit.Ceiling(l) : null, SoftLimit.State(usage, limit));
    }
}

/// <summary>The soft-limit check of creating handlers (ADR-0023), against the live count and the cached entitlements.</summary>
public sealed class LimitGuard(UsageMeter meter) : ILimitGuard
{
    public const string LimitReachedCode = "billing.limit_reached";

    public async Task<Error?> CheckCreateAsync(string metric, int count, CancellationToken cancellationToken)
    {
        var line = await meter.CurrentAsync(metric, cancellationToken).ConfigureAwait(false);
        return SoftLimit.Allows(line.Usage, line.Limit, count)
            ? null
            : Error.Forbidden(LimitReachedCode, $"The plan's {metric} limit is reached (with its 10 % grace); upgrade or remove some first.")
                .With("metric", metric).With("limit", line.Limit!.Value).With("usage", line.Usage);
    }
}

public sealed record UsageRun(int Tenants, int OverLimit, int Failed);

/// <summary>
/// The daily usage snapshot (US-P1-BIL-04 AC1): every tenant's metrics with the limit that applied, replaced when run
/// again the same day. A metric above its limit is logged (event 5002, the tenant and the numbers, no personal data)
/// and counted in <c>karry.billing.usage.over_limit</c> (alert <c>KarryTenantOverLimit</c>): Karry's notice (AC2).
/// </summary>
public sealed partial class SnapshotUsage(IServiceScopeFactory scopes, TimeProvider clock, UsageMetrics metrics, ILogger<SnapshotUsage> logger)
{
    public async Task<UsageRun> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SharedKernel.Tenancy.TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var over = 0;
        var failed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                var lines = await scope.ServiceProvider.GetRequiredService<UsageMeter>().CurrentAsync(cancellationToken).ConfigureAwait(false);
                var store = scope.ServiceProvider.GetRequiredService<IUsageStore>();
                var now = clock.GetUtcNow();
                var existing = await store.OnDayAsync(DateOnly.FromDateTime(now.UtcDateTime), cancellationToken).ConfigureAwait(false);
                foreach (var line in lines)
                {
                    if (existing.SingleOrDefault(s => s.Metric == line.Metric) is { } snapshot)
                    {
                        snapshot.Update(now, line.Usage, line.Limit);
                    }
                    else
                    {
                        store.Add(UsageSnapshot.Take(tenant, line.Metric, now, line.Usage, line.Limit));
                    }

                    if (line.State != SoftLimit.Within)
                    {
                        over++;
                        LogOverLimit(logger, tenant.ToString(), line.Metric, line.Usage, line.Limit!.Value, line.State);
                    }
                }

                await store.SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogTenantFailed(logger, ex, tenant.ToString());
            }
        }

        metrics.RunCompleted(over, failed == 0);
        return new UsageRun(tenants.Count, over, failed);
    }

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning,
        Message = "Tenant {Tenant} is over its {Metric} limit: {Usage} of {Limit} ({State}); tell the customer about an upgrade")]
    private static partial void LogOverLimit(ILogger logger, string tenant, string metric, long usage, long limit, string state);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Error, Message = "Usage snapshot failed for tenant {Tenant}; the next run retries")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, string tenant);
}

/// <summary>Usage telemetry: metrics above their limit at the last snapshot (no tenant label: the log names them).</summary>
public sealed class UsageMetrics
{
    public const string MeterName = "Karry.Billing";

    private long overLimit;
    private long lastSuccessUnixSeconds;

    public UsageMetrics(IMeterFactory meterFactory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Clock = clock;
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge("karry.billing.usage.over_limit", () => Interlocked.Read(ref overLimit),
            "{metric}", "Tenant metrics above their plan limit at the last usage snapshot.");
        meter.CreateObservableGauge("karry.billing.usage.last_success", () =>
            Interlocked.Read(ref lastSuccessUnixSeconds) is var at and > 0 ? [new Measurement<long>(at)] : Array.Empty<Measurement<long>>(),
            "s", "Unix time of the last usage snapshot in which every tenant succeeded.");
    }

    private TimeProvider Clock { get; }

    public long OverLimit => Interlocked.Read(ref overLimit);

    public void RunCompleted(int over, bool allSucceeded)
    {
        Interlocked.Exchange(ref overLimit, over);
        if (allSucceeded)
        {
            Interlocked.Exchange(ref lastSuccessUnixSeconds, Clock.GetUtcNow().ToUnixTimeSeconds());
        }
    }
}
