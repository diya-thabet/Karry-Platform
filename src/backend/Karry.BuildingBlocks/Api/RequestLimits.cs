using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Karry.BuildingBlocks.Api;

/// <summary>
/// Request bodies are at most <see cref="DefaultMaxBodyBytes"/> (hardening plan H-A4), unless the endpoint declares
/// its own limit with <see cref="WithMaxBody{TBuilder}"/> (uploads, sync batches). The limit is set before anything
/// reads the body (idempotency hashes it), so a large body is refused early: by its <c>Content-Length</c> at once,
/// and by the server while it streams in otherwise. Either way the answer is <c>413 request.too_large</c>.
/// </summary>
public static class RequestLimits
{
    /// <summary>1 MiB: far above any JSON command or form of the API.</summary>
    public const long DefaultMaxBodyBytes = 1024 * 1024;

    public const string TooLargeCode = "request.too_large";

    /// <summary>
    /// The endpoint takes bodies up to <paramref name="bytes"/>. For an upload whose module checks its own size with a
    /// clearer message (a logo, a photo, a workbook), pass twice that size so the module's check answers first.
    /// </summary>
    public static TBuilder WithMaxBody<TBuilder>(this TBuilder builder, long bytes)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequestSizeLimitAttribute(bytes));

    internal static IApplicationBuilder UseKarryRequestLimits(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var limit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>() is { } declared
            ? declared.MaxRequestBodySize
            : DefaultMaxBodyBytes;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        if (limit is { } max && context.Request.ContentLength > max)
        {
            await TooLarge(context, max).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    });

    internal static IResult TooLarge(HttpContext context, long? max) => TypedResults.Problem(
        title: "The request body is too large.",
        statusCode: StatusCodes.Status413PayloadTooLarge,
        type: ProblemTypes.For(TooLargeCode).ToString(),
        extensions: new Dictionary<string, object?> { ["code"] = TooLargeCode, ["maxBytes"] = max ?? (context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize) });
}
