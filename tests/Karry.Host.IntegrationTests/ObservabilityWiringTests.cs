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
        var spans = new List<Activity>();
        await using var factory = new HostFactory(connectionString: null).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(spans))));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var span = await WaitForServerSpanAsync(spans);

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
        var spans = new List<Activity>();
        await using var factory = new HostFactory(connectionString: null).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(spans))));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        await WaitForServerSpanAsync(spans); // the later request is traced, so the probe would be too if it were

        response.Headers.Contains(RequestTelemetryMiddleware.CorrelationHeader).Should().BeTrue();
        spans.Should().NotContain(s => Equals(s.GetTagItem(TelemetryAttributes.Route), "/health/live"));
    }

    private static async Task<Activity> WaitForServerSpanAsync(List<Activity> spans)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (spans)
            {
                if (spans.FirstOrDefault(s => s.Kind == ActivityKind.Server) is { } span)
                {
                    return span;
                }
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("No server span exported.");
    }
}
