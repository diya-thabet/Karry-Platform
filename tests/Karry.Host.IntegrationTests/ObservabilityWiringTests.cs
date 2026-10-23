using System.Diagnostics;
using Karry.BuildingBlocks.Observability;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Karry.Host.IntegrationTests;

/// <summary>The real <c>Program.cs</c> wires telemetry and correlation (the building blocks are tested in BuildingBlocks.UnitTests).</summary>
[Trait("Story", "US-P1-OPS-01")]
public sealed class ObservabilityWiringTests
{
    [Fact]
    [Trait("TestCase", "TC-P1-OPS-003")]
    public async Task The_api_host_traces_requests_with_karry_attributes_and_returns_the_correlation_id()
    {
        var spans = new SpanCapture();
        await using var factory = new HostFactory(connectionString: null).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.ConfigureOpenTelemetryTracerProvider(t => t.AddProcessor(new SimpleActivityExportProcessor(spans)))));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var span = await WaitForServerSpanAsync(spans, response);

        span.GetTagItem(TelemetryAttributes.Module).Should().Be(TelemetryAttributes.HostModule);
        span.GetTagItem(TelemetryAttributes.Route).Should().Be("/openapi/{documentName}.json");
        span.GetTagItem(TelemetryAttributes.TenantId).Should().Be(TelemetryAttributes.NoTenant);
        span.GetTagItem(TelemetryAttributes.UserRole).Should().Be(TelemetryAttributes.AnonymousRole);
        factory.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .Should().Contain(new KeyValuePair<string, object>("service.name", "karry-api"));
        response.Headers.GetValues(RequestTelemetryMiddleware.CorrelationHeader).Should().Equal(span.TraceId.ToString());
    }

    [Fact]
    public async Task Health_probes_are_not_traced_but_still_return_a_correlation_id()
    {
        var spans = new SpanCapture();
        await using var factory = new HostFactory(connectionString: null).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.ConfigureOpenTelemetryTracerProvider(t => t.AddProcessor(new SimpleActivityExportProcessor(spans)))));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        var later = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        await WaitForServerSpanAsync(spans, later); // the later request is traced, so the probe would be too if it were

        var probeTraceId = response.Headers.GetValues(RequestTelemetryMiddleware.CorrelationHeader).Single();
        spans.Snapshot().Should().NotContain(s => s.TraceId.ToString() == probeTraceId);
    }

    /// <summary>The server span of the response's trace (listeners are process-wide: other tests' spans arrive too).</summary>
    private static async Task<Activity> WaitForServerSpanAsync(SpanCapture spans, HttpResponseMessage response)
    {
        var traceId = response.Headers.GetValues(RequestTelemetryMiddleware.CorrelationHeader).Single();
        for (var i = 0; i < 100; i++)
        {
            if (spans.Snapshot().FirstOrDefault(s => s.Kind == ActivityKind.Server && s.TraceId.ToString() == traceId) is { } span)
            {
                return span;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"No server span exported for trace {traceId}.");
    }

    /// <summary>Thread-safe span sink: exporters run on whichever thread ends an activity.</summary>
    private sealed class SpanCapture : BaseExporter<Activity>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Activity> exported = new();

        public Activity[] Snapshot() => [.. exported];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                exported.Enqueue(activity);
            }

            return ExportResult.Success;
        }
    }
}
