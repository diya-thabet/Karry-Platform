using System.Diagnostics.Metrics;
using Karry.Modules.Trust.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Trust.Tests;

[Trait("Story", "US-P2-SIG-03")]
public sealed class CountersignMetricsTests
{
    [Fact]
    public void The_lag_is_reported_only_after_a_run_and_the_counters_count()
    {
        using var services = new ServiceCollection().AddMetrics().AddSingleton<CountersignMetrics>().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var seen = new Dictionary<string, double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CountersignMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => seen[instrument.Name] = seen.GetValueOrDefault(instrument.Name) + value);
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => seen[instrument.Name] = value);
        listener.Start();
        var metrics = services.GetRequiredService<CountersignMetrics>();

        listener.RecordObservableInstruments();
        var before = seen.ContainsKey("karry.trust.countersign_lag");
        metrics.Countersigned();
        metrics.Countersigned();
        metrics.Failed();
        metrics.Measured(TimeSpan.FromMinutes(16));
        listener.RecordObservableInstruments();

        before.Should().BeFalse("an alert must not fire on a gauge that was never measured");
        seen.Should().BeEquivalentTo(new Dictionary<string, double>
        {
            ["karry.trust.countersigned"] = 2,
            ["karry.trust.countersign_failures"] = 1,
            ["karry.trust.countersign_lag"] = 960,
        });
    }
}
