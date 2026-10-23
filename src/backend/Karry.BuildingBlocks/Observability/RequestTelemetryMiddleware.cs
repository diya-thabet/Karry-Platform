using System.Diagnostics;
using Karry.BuildingBlocks.Modules;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Karry.BuildingBlocks.Observability;

/// <summary>
/// Per request: returns the correlation headers (US-P1-OPS-01 AC3) and tags the server span and the HTTP metrics with
/// the Karry attributes (AC1). Runs outermost, so error responses (including the generic 500) carry the headers too.
/// </summary>
public sealed class RequestTelemetryMiddleware(RequestDelegate next)
{
    /// <summary>Response header with the trace ID; the same value as <c>traceId</c> in problem details.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>W3C Trace Context header of the server span (lets a client or proxy join the trace).</summary>
    public const string TraceParentHeader = "traceparent";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var activity = Activity.Current;
        context.Response.OnStarting(() =>
        {
            // Set when the response starts: the exception handler clears headers set earlier.
            var headers = context.Response.Headers;
            headers[CorrelationHeader] = activity?.TraceId.ToString() ?? context.TraceIdentifier;
            if (activity is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent })
            {
                headers[TraceParentHeader] = traceParent;
            }

            return Task.CompletedTask;
        });

        var (module, route) = Describe(context.GetEndpoint());
        if (activity is not null)
        {
            activity.SetTag(TelemetryAttributes.Module, module);
            activity.SetTag(TelemetryAttributes.Route, route);
        }

        // RED metrics per module: adds "module" to http.server.request.duration (low cardinality, no tenant).
        context.Features.Get<IHttpMetricsTagsFeature>()?.Tags.Add(new(TelemetryAttributes.Module, module));

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            // Tenancy (P1.5) and authentication (P1.8) set the real values on the span; defaults keep the attributes
            // always present so dashboards and the TC-P1-OPS-003 contract do not depend on the endpoint.
            if (activity is not null)
            {
                activity.SetTag(TelemetryAttributes.TenantId, activity.GetTagItem(TelemetryAttributes.TenantId) ?? TelemetryAttributes.NoTenant);
                activity.SetTag(TelemetryAttributes.UserRole, activity.GetTagItem(TelemetryAttributes.UserRole) ?? TelemetryAttributes.AnonymousRole);
            }
        }
    }

    private static (string Module, string Route) Describe(Endpoint? endpoint)
    {
        if (endpoint is null)
        {
            return (TelemetryAttributes.Unmatched, TelemetryAttributes.Unmatched);
        }

        var module = endpoint.Metadata.GetMetadata<ModuleMetadata>()?.Name ?? TelemetryAttributes.HostModule;
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? TelemetryAttributes.Unmatched;
        return (module, route);
    }
}
