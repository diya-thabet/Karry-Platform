using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Karry.BuildingBlocks.Outbox;

/// <summary>
/// Outbox telemetry (US-P1-OPS-01 AC2, alert <c>KarryOutboxLagHigh</c>): messages by outcome, and the age of the oldest
/// pending message per module, the "job lag" that tells whether events reach their subscribers.
/// </summary>
public sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "Karry.Outbox";

    /// <summary>Spans of message dispatch (one per message).</summary>
    internal static readonly ActivitySource ActivitySource = new(MeterName);

    private readonly Meter meter;
    private readonly Counter<long> messages;
    private readonly ConcurrentDictionary<string, double> oldestPendingAge = new(StringComparer.Ordinal);

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        meter = meterFactory.Create(MeterName);
        messages = meter.CreateCounter<long>(
            "karry.outbox.messages", "{message}", "Outbox messages dispatched, by module and outcome (processed, retried, dead_lettered).");
        meter.CreateObservableGauge(
            "karry.outbox.oldest_pending_age",
            () => oldestPendingAge.Select(entry => new Measurement<double>(entry.Value, new KeyValuePair<string, object?>("module", entry.Key))),
            "s",
            "Age of the oldest message not yet processed or dead-lettered (0 when the outbox is empty).");
    }

    internal void Record(string module, string outcome) =>
        messages.Add(1, new KeyValuePair<string, object?>("module", module), new KeyValuePair<string, object?>("outcome", outcome));

    internal void SetOldestPendingAge(string module, TimeSpan age) =>
        oldestPendingAge[module] = Math.Max(0, age.TotalSeconds);

    public void Dispose() => meter.Dispose();
}
