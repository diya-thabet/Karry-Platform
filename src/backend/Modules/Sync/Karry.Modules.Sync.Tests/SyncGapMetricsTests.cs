using System.Diagnostics.Metrics;
using Karry.Modules.Sync.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Sync.Tests;

[Trait("Story", "US-P2-OFF-03")]
public sealed class SyncGapMetricsTests
{
    [Fact]
    public void The_gauges_report_the_last_measurement_and_nothing_before_the_first()
    {
        using var services = new ServiceCollection().AddMetrics().AddSingleton<SyncGapMetrics>().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var seen = new Dictionary<string, double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SyncGapMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => seen[instrument.Name] = value);
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => seen[instrument.Name] = value);
        listener.Start();
        var metrics = services.GetRequiredService<SyncGapMetrics>();

        listener.RecordObservableInstruments();
        var before = seen.Count;
        metrics.Measured(7, TimeSpan.FromMinutes(90));
        listener.RecordObservableInstruments();
        var during = new Dictionary<string, double>(seen);
        metrics.Measured(0, null);
        listener.RecordObservableInstruments();

        before.Should().Be(0, "an alert must not fire on a gauge that was never measured");
        during.Should().BeEquivalentTo(new Dictionary<string, double> { ["karry.sync.waiting_commands"] = 7, ["karry.sync.oldest_waiting_age"] = 5_400 });
        seen.Should().BeEquivalentTo(new Dictionary<string, double> { ["karry.sync.waiting_commands"] = 0, ["karry.sync.oldest_waiting_age"] = 0 });
    }
}
