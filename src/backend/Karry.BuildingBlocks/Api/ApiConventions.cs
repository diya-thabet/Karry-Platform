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
    public static IServiceCollection AddKarryApiConventions(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            // Framework-generated problems (404 route, 405, 415, bad JSON) get the same shape as ours.
            // Overwrite ASP.NET Core's default (the full traceparent): support needs the same ID as X-Correlation-Id.
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
            context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
            {
                StatusCodes.Status400BadRequest => ProblemTypes.ValidationCode,
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
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Karry.Api");
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
        return app;
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Unhandled exception on {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, string method, PathString path, Exception? exception);
}
