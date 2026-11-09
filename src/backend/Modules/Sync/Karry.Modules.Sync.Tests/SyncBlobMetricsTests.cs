using System.Diagnostics.Metrics;
using Karry.Modules.Sync.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Sync.Tests;

[Trait("Story", "US-P2-OFF-01")]
public sealed class SyncBlobMetricsTests
{
    [Fact]
    public void Each_blob_is_counted_once_under_its_result()
    {
        using var services = new ServiceCollection().AddMetrics().AddSingleton<SyncBlobMetrics>().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var seen = new List<(string Name, long Value, string? Result)>();
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
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            seen.Add((instrument.Name, value, tags.ToArray().Single(t => t.Key == "result").Value as string)));
        listener.Start();
        var metrics = services.GetRequiredService<SyncBlobMetrics>();

        metrics.Count(SyncBlobMetrics.Presigned);
        metrics.Count(SyncBlobMetrics.Mismatch);

        seen.Should().Equal(("karry.sync.blobs", 1L, "presigned"), ("karry.sync.blobs", 1L, "mismatch"));
    }
}
