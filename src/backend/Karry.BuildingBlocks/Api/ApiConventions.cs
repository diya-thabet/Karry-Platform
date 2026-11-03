using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.BuildingBlocks.Api;

/// <summary>Problem-details error handling shared by every host.</summary>
public static partial class ApiConventions
{
    /// <summary>
    /// CORS policy for anonymous, public GET endpoints read from other origins (the Keycloak login page reads tenant
    /// branding): any origin, never credentials. Endpoints opt in with <see cref="AllowPublicCrossOrigin{TBuilder}"/>.
    /// </summary>
    public const string PublicCorsPolicy = "karry-public";

    public static IServiceCollection AddKarryApiConventions(this IServiceCollection services)
    {
        services.AddKarrySecurityHeaders();
        services.AddCors(options => options.AddPolicy(PublicCorsPolicy, policy => policy.AllowAnyOrigin().WithMethods("GET").WithHeaders("Accept")));
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            // Framework-generated problems (404 route, 405, 415, bad JSON) get the same shape as ours.
            // Overwrite ASP.NET Core's default (the full traceparent): support needs the same ID as X-Correlation-Id.
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
            context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
            {
                StatusCodes.Status400BadRequest => ProblemTypes.ValidationCode,
                StatusCodes.Status401Unauthorized => Tenancy.TenantResolutionMiddleware.UnauthenticatedCode, // no or bad token
                StatusCodes.Status403Forbidden => Authorization.PermissionMiddleware.ForbiddenCode, // a policy refused (operator only)
                StatusCodes.Status404NotFound => "route.not_found",
                StatusCodes.Status405MethodNotAllowed => "route.method_not_allowed",
                StatusCodes.Status415UnsupportedMediaType => "request.unsupported_media_type",
                _ => ProblemTypes.UnexpectedCode,
            });
        });
        return services;
    }

    /// <summary>
    /// Unhandled exceptions become a generic 500 problem: no exception type, message or stack trace ever reaches
    /// the client (they are logged with the trace ID instead).
    /// </summary>
    public static IApplicationBuilder UseKarryApiConventions(this IApplicationBuilder app)
    {
        // First, so every answer carries them: errors, 404s, 413s and rate-limit refusals included.
        app.UseKarrySecurityHeaders();
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;

            // The client's fault, not ours: a body past the limit while it streamed in, or a body that could not be read.
            if (exception is BadHttpRequestException bad)
            {
                await (bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                    ? RequestLimits.TooLarge(context, null)
                    : TypedResults.Problem(title: "The request could not be read.", statusCode: StatusCodes.Status400BadRequest,
                        type: ProblemTypes.For(ProblemTypes.ValidationCode).ToString(),
                        extensions: new Dictionary<string, object?> { ["code"] = ProblemTypes.ValidationCode })).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Karry.Api");

            // A dependency down for a moment is not a bug: 503 and when to come back, so tablets and the portal retry later.
            if (Outages.Describe(exception) is { } dependency)
            {
                LogDependencyDown(logger, context.Request.Method, context.Request.Path, dependency, exception);
                context.Response.Headers.RetryAfter = Outages.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await TypedResults.Problem(title: "The service is briefly unavailable. Try again in a minute.", statusCode: StatusCodes.Status503ServiceUnavailable,
                    type: ProblemTypes.For(ProblemTypes.UnavailableCode).ToString(),
                    extensions: new Dictionary<string, object?> { ["code"] = ProblemTypes.UnavailableCode }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            LogUnhandled(logger, context.Request.Method, context.Request.Path, exception);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "An unexpected error occurred. Quote the trace ID when contacting support.",
                    Type = ProblemTypes.For(ProblemTypes.UnexpectedCode).ToString(),
                    Extensions = { ["code"] = ProblemTypes.UnexpectedCode },
                },
            }).ConfigureAwait(false);
        }));
        app.UseStatusCodePages();
        app.UseKarryRequestLimits();

        // Only endpoints with a CORS policy answer cross-origin requests; the default is same-origin only.
        app.UseCors();
        return app;
    }

    /// <summary>Lets any origin read this anonymous GET endpoint (no cookies or tokens are ever sent cross-origin).</summary>
    public static TBuilder AllowPublicCrossOrigin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireCors(PublicCorsPolicy);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "{Dependency} unavailable on {Method} {Path}; answered 503")]
    private static partial void LogDependencyDown(ILogger logger, string method, PathString path, string dependency, Exception? exception);

    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Unhandled exception on {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, string method, PathString path, Exception? exception);
}
