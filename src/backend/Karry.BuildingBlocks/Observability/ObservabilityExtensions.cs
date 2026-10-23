using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Karry.BuildingBlocks.Observability;

/// <summary>
/// OpenTelemetry for every Karry host (US-P1-OPS-01): traces, metrics and logs, exported with OTLP when
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured (local stack: Grafana LGTM; production: P1.17). Without an endpoint
/// the signals are still produced (tests attach in-memory exporters) but nothing is sent anywhere.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>Configuration key of the OTLP endpoint (standard OpenTelemetry environment variable).</summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Prefix of Karry's own activity sources and meters (<c>Karry.Outbox</c>, …).</summary>
    public const string KarrySources = "Karry.*";

    public static IHostApplicationBuilder AddKarryObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var exportOtlp = !string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey]);
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: version)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options => options.Filter = IsTraced)
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddSource(KarrySources)
                    // First processor: everything exported after it is already allow-listed.
                    .AddProcessor(new AttributeAllowListProcessor());
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Npgsql") // connection pool: usage, max, pending requests, wait time
                    .AddMeter(KarrySources);
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            })
            .WithLogging(
                logging =>
                {
                    if (exportOtlp)
                    {
                        logging.AddOtlpExporter();
                    }
                },
                options =>
                {
                    options.IncludeScopes = false; // scopes repeat the request path; log records already carry trace and span IDs
                    options.IncludeFormattedMessage = true;
                });

        // "Request starting GET /…?email=…" logs the full URL with its query string: keep it out of exported logs.
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        return builder;
    }

    /// <summary>Correlation headers and Karry span attributes; call first in the pipeline.</summary>
    public static IApplicationBuilder UseKarryObservability(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestTelemetryMiddleware>();

    /// <summary>Health probes run every few seconds and would drown real traffic.</summary>
    private static bool IsTraced(HttpContext context) => !context.Request.Path.StartsWithSegments("/health");
}
