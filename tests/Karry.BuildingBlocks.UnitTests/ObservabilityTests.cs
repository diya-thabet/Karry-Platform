using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// The real observability setup (<see cref="ObservabilityExtensions"/>) on an in-memory host with in-memory exporters:
/// what leaves the process is exactly what these tests see.
/// </summary>
[Trait("Story", "US-P1-OPS-01")]
public sealed partial class ObservabilityTests : IAsyncLifetime
{
    // Personal data a real request can carry: in the query string, in headers, in the body.
    private const string Email = "jane.doe@example.com";
    private const string Name = "Jane Doe";
    private const string Phone = "+216 98 765 432";
    private const string Token = "eyJhbGciOiJSUzI1NiJ9.secret-access-token";

    private readonly SpanCapture spans = new();
    private readonly List<Metric> metrics = [];
    private readonly LogCapture logs = new();
    private WebApplication? app;
    private HttpClient? client;

    public sealed record Signup(string Email, string Name, string Phone);

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddKarryObservability("karry-test");
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(spans)));
        builder.Services.ConfigureOpenTelemetryMeterProvider(meters => meters.AddInMemoryExporter(metrics));
        builder.Services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddProcessor(new SimpleLogRecordExportProcessor(logs)));
        builder.Services.AddKarryApiConventions();
        builder.Services.AddModules(builder.Configuration, [new ProbeModule()]);
        app = builder.Build();
        app.UseKarryObservability();
        app.UseKarryApiConventions();
        app.MapModules();
        app.MapGet("/boom", IResult () => throw new InvalidOperationException("boom"));

        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    [Trait("TestCase", "TC-P1-OPS-003")]
    public async Task A_request_is_traced_with_the_four_karry_attributes_and_no_personal_data()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/probe/signups/S-42?email={Uri.EscapeDataString(Email)}&phone={Uri.EscapeDataString(Phone)}")
        {
            Content = JsonContent.Create(new Signup(Email, Name, Phone)),
        };
        request.Headers.Authorization = new("Bearer", Token);
        request.Headers.TryAddWithoutValidation("User-Agent", $"KarryField/1.0 ({Name}; {Email})");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "196.203.10.7");

        var response = await client!.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var server = await ServerSpanAsync(response, "/api/v1/probe/signups/{code}");

        server.GetTagItem(TelemetryAttributes.TenantId).Should().Be(TelemetryAttributes.NoTenant);
        server.GetTagItem(TelemetryAttributes.Module).Should().Be("probe");
        server.GetTagItem(TelemetryAttributes.Route).Should().Be("/api/v1/probe/signups/{code}");
        server.GetTagItem(TelemetryAttributes.UserRole).Should().Be(TelemetryAttributes.AnonymousRole);

        string[] personalData = [Email, Name, Phone, Token, "jane.doe", "98 765 432", "secret-access-token", "196.203.10.7", "S-42"];
        foreach (var span in spans.OfTrace(server.TraceId))
        {
            span.TagObjects.Select(t => t.Key).Should().OnlyContain(key => TelemetryAttributes.Allowed.Contains(key), $"span '{span.DisplayName}' exports only allow-listed attributes");
            span.TagObjects.Select(t => Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
                .Should().NotContain(value => personalData.Any(value.Contains), $"span '{span.DisplayName}' must not carry personal data");
            span.DisplayName.Should().NotContainAny(personalData);
            span.Events.SelectMany(e => e.Tags).Select(t => t.Value?.ToString() ?? string.Empty).Should().NotContain(value => personalData.Any(value.Contains));
        }

        logs.Records.Should().NotBeEmpty("the endpoint logs, so the log pipeline is really exercised");
        logs.Records.Should().NotContain(text => personalData.Any(text.Contains), "exported logs must not carry personal data");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-OPS-003")]
    public async Task Requests_outside_modules_are_attributed_to_the_host_or_unmatched()
    {
        var failed = await client!.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);
        var missing = await client.GetAsync(new Uri("/nowhere/jane.doe@example.com", UriKind.Relative), TestContext.Current.CancellationToken);
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var boom = await ServerSpanAsync(failed, "/boom");
        boom.GetTagItem(TelemetryAttributes.Module).Should().Be(TelemetryAttributes.HostModule);
        boom.Status.Should().Be(ActivityStatusCode.Error);

        var unmatched = await ServerSpanAsync(missing, TelemetryAttributes.Unmatched);
        unmatched.GetTagItem(TelemetryAttributes.Module).Should().Be(TelemetryAttributes.Unmatched);
        unmatched.GetTagItem(TelemetryAttributes.TenantId).Should().Be(TelemetryAttributes.NoTenant);
        unmatched.DisplayName.Should().NotContain("jane.doe", "the concrete path is never exported");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-OPS-005")]
    public async Task Every_response_returns_the_trace_id_as_correlation_id_and_a_traceparent()
    {
        var response = await client!.PostAsJsonAsync("/api/v1/probe/signups/S-1", new Signup("a@b.c", "A", "1"), TestContext.Current.CancellationToken);
        var server = await ServerSpanAsync(response, "/api/v1/probe/signups/{code}");

        response.Headers.GetValues(RequestTelemetryMiddleware.CorrelationHeader).Should().Equal(server.TraceId.ToString());
        response.Headers.GetValues(RequestTelemetryMiddleware.TraceParentHeader).Should().Equal(server.Id);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-OPS-005")]
    public async Task An_error_response_carries_the_same_correlation_id_in_headers_and_problem_details()
    {
        var response = await client!.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        var server = await ServerSpanAsync(response, "/boom");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var correlationId = response.Headers.GetValues(RequestTelemetryMiddleware.CorrelationHeader).Should().ContainSingle().Subject;
        correlationId.Should().Be(server.TraceId.ToString());
        problem.GetProperty("traceId").GetString().Should().Be(correlationId, "support matches the ID the user quotes with the trace");
        response.Headers.Contains(RequestTelemetryMiddleware.TraceParentHeader).Should().BeTrue();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-OPS-005")]
    public async Task A_caller_traceparent_is_continued_so_client_and_server_share_one_trace()
    {
        const string clientTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/boom");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{clientTraceId}-00f067aa0ba902b7-01");

        var response = await client!.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues(RequestTelemetryMiddleware.CorrelationHeader).Should().Equal(clientTraceId);
    }

    [Fact]
    public async Task Request_metrics_carry_the_module_for_red_dashboards()
    {
        var response = await client!.PostAsJsonAsync("/api/v1/probe/signups/S-7", new Signup("a@b.c", "A", "1"), TestContext.Current.CancellationToken);
        await ServerSpanAsync(response, "/api/v1/probe/signups/{code}"); // the request is complete once its span ended

        app!.Services.GetRequiredService<MeterProvider>().ForceFlush().Should().BeTrue();

        var points = new List<MetricPoint>();
        foreach (var metric in metrics.Where(m => m.Name == "http.server.request.duration"))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                points.Add(point);
            }
        }

        points.Select(Tags).Should().Contain(tags =>
            Equals(tags.GetValueOrDefault("module"), "probe") && Equals(tags.GetValueOrDefault("http.route"), "/api/v1/probe/signups/{code}"));
    }

    [Fact]
    public void The_allow_list_processor_removes_every_attribute_it_does_not_know()
    {
        using var activity = new Activity("test");
        activity.SetTag(TelemetryAttributes.Module, "assets");
        activity.SetTag("http.response.status_code", 200);
        activity.SetTag("url.query", "email=jane.doe@example.com");
        activity.SetTag("enduser.id", "jane");
        using var processor = new AttributeAllowListProcessor();

        processor.OnEnd(activity);

        activity.TagObjects.Select(t => t.Key).Should().BeEquivalentTo(TelemetryAttributes.Module, "http.response.status_code");
    }

    private static Dictionary<string, object?> Tags(MetricPoint point)
    {
        var tags = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in point.Tags)
        {
            tags[tag.Key] = tag.Value;
        }

        return tags;
    }

    /// <summary>
    /// The server span of <paramref name="response"/>'s request, found by the trace ID the response returned. Activity
    /// listeners are process-wide, so spans of tests running in parallel arrive here too and must be filtered out.
    /// The span ends after the response is sent: wait for its export.
    /// </summary>
    private async Task<Activity> ServerSpanAsync(HttpResponseMessage response, string route)
    {
        var traceId = response.Headers.GetValues(RequestTelemetryMiddleware.CorrelationHeader).Single();
        for (var i = 0; i < 100; i++)
        {
            var span = spans.Snapshot().LastOrDefault(s =>
                s.Kind == ActivityKind.Server && s.TraceId.ToString() == traceId && Equals(s.GetTagItem(TelemetryAttributes.Route), route));
            if (span is not null)
            {
                return span;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"No server span for route {route} in trace {traceId}.");
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Signup {Code} accepted")]
    private static partial void LogSignup(ILogger logger, string code);

    /// <summary>Thread-safe span sink: exporters run on whichever thread ends an activity.</summary>
    private sealed class SpanCapture : BaseExporter<Activity>
    {
        private readonly ConcurrentQueue<Activity> exported = new();

        public Activity[] Snapshot() => [.. exported];

        public IEnumerable<Activity> OfTrace(ActivityTraceId traceId) => Snapshot().Where(s => s.TraceId == traceId);

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                exported.Enqueue(activity);
            }

            return ExportResult.Success;
        }
    }

    /// <summary>Snapshots everything a log exporter would send (records are pooled and reused after export).</summary>
    private sealed class LogCapture : BaseExporter<LogRecord>
    {
        public ConcurrentQueue<string> Records { get; } = new();

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var attributes = record.Attributes?.Select(a => $"{a.Key}={a.Value}") ?? [];
                Records.Enqueue(string.Join(" | ", [record.CategoryName, record.FormattedMessage, record.Body, .. attributes, record.Exception?.ToString()]));
            }

            return ExportResult.Success;
        }
    }

    private sealed class ProbeModule : IModule
    {
        public string Name => "probe";

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
            endpoints.MapPost("/signups/{code}", (string code, Signup signup, ILoggerFactory loggers) =>
            {
                // A well-behaved handler logs identifiers it owns, never the person's data. The route value "S-42" is
                // an identifier but the test still requires it to be absent: the concrete path is never exported.
                var logger = loggers.CreateLogger("Karry.Probe");
                LogSignup(logger, "signup");
                return TypedResults.Created($"/api/v1/probe/signups/{code}", signup.Name.Length);
            });
    }
}
