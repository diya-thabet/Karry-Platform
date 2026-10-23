using System.Diagnostics.Metrics;

namespace Karry.Modules.Trust.Application;

/// <summary>
/// Anchoring telemetry: runs by outcome, and the time of the last fully successful run (alert
/// <c>KarryLedgerAnchoringStale</c> when it is more than two hours old).
/// </summary>
public sealed class AnchoringMetrics
{
    public const string MeterName = "Karry.Ledger";

    private readonly Counter<long> runs;
    private long lastSuccessUnixSeconds;

    public AnchoringMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        runs = meter.CreateCounter<long>("karry.ledger.anchor.runs", "{run}", "Anchoring runs by outcome (success, partial_failure).");
        meter.CreateObservableGauge("karry.ledger.anchor.last_success", () =>
            Interlocked.Read(ref lastSuccessUnixSeconds) is var at and > 0 ? [new Measurement<long>(at)] : Array.Empty<Measurement<long>>(),
            "s", "Unix time of the last anchoring run in which every tenant succeeded.");
    }

    public void RunCompleted(bool allSucceeded, DateTimeOffset at)
    {
        runs.Add(1, new KeyValuePair<string, object?>("outcome", allSucceeded ? "success" : "partial_failure"));
        if (allSucceeded)
        {
            Interlocked.Exchange(ref lastSuccessUnixSeconds, at.ToUnixTimeSeconds());
        }
    }
}
