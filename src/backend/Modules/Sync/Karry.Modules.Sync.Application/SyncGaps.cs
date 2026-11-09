using System.Diagnostics.Metrics;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Sync.Application;

/// <summary>A tablet with commands waiting for an earlier number (ADR-0031 §4), and since when.</summary>
public sealed record DeviceGap(Guid DeviceId, int Waiting, DateTimeOffset OldestReceivedAt);

public interface IWaitingReport
{
    /// <summary>The current tenant's tablets with waiting commands (row-level security scopes it).</summary>
    Task<IReadOnlyList<DeviceGap>> DevicesAsync(CancellationToken cancellationToken);
}

/// <summary>One measurement: waiting commands across tenants, the oldest one's age, and the tablets past the limit.</summary>
public sealed record SyncGapRun(int Tenants, int Waiting, TimeSpan? OldestAge, IReadOnlyList<DeviceGap> Overdue, int Failed);

/// <summary>
/// Measures sequence gaps (ADR-0031 §4): how many commands wait for an earlier number, and how long the oldest has
/// waited. A tablet whose gap is open longer than <see cref="AlertAfter"/> may have lost data and is logged (event
/// 7206); the gauges feed the alert <c>KarrySyncGapOpen</c>. A failing tenant is logged (7207) and the others still run.
/// </summary>
public sealed partial class MeasureSyncGaps(IServiceScopeFactory scopes, SyncGapMetrics metrics, TimeProvider clock, ILogger<MeasureSyncGaps> logger)
{
    public static readonly TimeSpan AlertAfter = TimeSpan.FromHours(24);

    public async Task<SyncGapRun> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SharedKernel.Tenancy.TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow();
        var waiting = 0;
        DateTimeOffset? oldest = null;
        var overdue = new List<DeviceGap>();
        var failed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                foreach (var device in await scope.ServiceProvider.GetRequiredService<IWaitingReport>().DevicesAsync(cancellationToken).ConfigureAwait(false))
                {
                    waiting += device.Waiting;
                    oldest = oldest is { } o && o <= device.OldestReceivedAt ? o : device.OldestReceivedAt;
                    if (now - device.OldestReceivedAt > AlertAfter)
                    {
                        overdue.Add(device);
                        LogOverdue(logger, tenant.ToString(), device.DeviceId, device.Waiting, (now - device.OldestReceivedAt).TotalHours);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogTenantFailed(logger, ex, tenant.ToString());
            }
        }

        var age = oldest is { } first ? now - first : (TimeSpan?)null;
        metrics.Measured(waiting, age);
        return new SyncGapRun(tenants.Count, waiting, age, overdue, failed);
    }

    [LoggerMessage(EventId = 7206, Level = LogLevel.Warning,
        Message = "Tablet {DeviceId} of tenant {Tenant} has {Waiting} command(s) waiting for an earlier number for {Hours:F1} h: data may be lost on it")]
    private static partial void LogOverdue(ILogger logger, string tenant, Guid deviceId, int waiting, double hours);

    [LoggerMessage(EventId = 7207, Level = LogLevel.Error, Message = "Sequence gaps of tenant {Tenant} could not be measured")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, string tenant);
}

/// <summary>Sync gap telemetry, from the last measurement: nothing is reported before the first one.</summary>
public sealed class SyncGapMetrics
{
    public const string MeterName = "Karry.Sync";

    private readonly Lock gate = new();
    private (long Waiting, double OldestAgeSeconds)? last;

    public SyncGapMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge("karry.sync.waiting_commands", () => Read(v => new Measurement<long>(v.Waiting)),
            "{command}", "Commands of every tablet waiting for an earlier number (ADR-0031 §4).");
        meter.CreateObservableGauge("karry.sync.oldest_waiting_age", () => Read(v => new Measurement<double>(v.OldestAgeSeconds)),
            "s", "How long the oldest waiting command has waited; 0 when none waits.");
    }

    public void Measured(int waiting, TimeSpan? oldestAge)
    {
        lock (gate)
        {
            last = (waiting, oldestAge?.TotalSeconds ?? 0);
        }
    }

    private Measurement<T>[] Read<T>(Func<(long Waiting, double OldestAgeSeconds), Measurement<T>> measure)
        where T : struct
    {
        lock (gate)
        {
            return last is { } value ? [measure(value)] : [];
        }
    }
}
