using System.Globalization;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.BuildingBlocks.Authorization;

/// <summary>
/// Permission check of every tenant endpoint (US-P1-IAM-05/06, ADR-0022). Runs after tenant resolution, before
/// idempotency and the endpoint. An endpoint that declares neither <see cref="PermissionRequirement"/> nor
/// <see cref="AnyTenantMember"/> is refused (fail closed): forgetting the declaration can never open an endpoint.
/// Resolves the caller's access once and stores it in <see cref="ICallerAccess"/> for scope filters and masking.
/// </summary>
public sealed partial class PermissionMiddleware(RequestDelegate next, ILogger<PermissionMiddleware> logger)
{
    public const string ForbiddenCode = "auth.forbidden";

    public const string UndeclaredCode = "auth.permission_undeclared";

    public const string StepUpCode = "auth.step_up_required";

    public const string AuthTimeClaim = "auth_time";

    public const string RevokedDeviceCode = "devices.revoked";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata;
        var services = context.RequestServices;
        if (metadata?.GetMetadata<TenantRequired>() is null || metadata.GetMetadata<AllowWithoutTenant>() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Tablets hold no roles: a device endpoint needs an enrolled device, and an active one unless it quarantines.
        if (metadata.GetMetadata<DeviceEndpoint>() is { } deviceEndpoint)
        {
            if (DeviceCaller.From(context.User) is not { } device)
            {
                await Forbidden(ForbiddenCode, "Only an enrolled tablet can do this.").ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            if (device.Revoked && !deviceEndpoint.AllowRevoked)
            {
                await TypedResults.Problem(
                    title: "This tablet has been revoked.", statusCode: StatusCodes.Status401Unauthorized,
                    type: ProblemTypes.For(RevokedDeviceCode).ToString(),
                    extensions: new Dictionary<string, object?> { ["code"] = RevokedDeviceCode }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        var required = metadata.GetOrderedMetadata<PermissionRequirement>();
        if (required.Count == 0 && metadata.GetMetadata<AnyTenantMember>() is null)
        {
            LogUndeclared(logger, endpoint!.DisplayName);
            await Forbidden(UndeclaredCode, "This operation declares no permission and is refused.").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var catalog = services.GetService<AuthorizationCatalog>() ?? AuthorizationCatalog.Empty;
        var subject = context.User.FindFirst("sub")?.Value;
        var signedInAt = SignedInAt(context);
        var access = services.GetService<ITenantContext>()?.Current is { } tenant && !string.IsNullOrEmpty(subject)
            && services.GetService<IAccessResolver>() is { } resolver
            ? CallerAccess.From(catalog, await resolver.AssignmentsAsync(tenant, subject, context.RequestAborted).ConfigureAwait(false))
            : CallerAccess.Nobody;
        services.GetRequiredService<ICallerAccess>().Set(access, subject, signedInAt);

        foreach (var requirement in required)
        {
            if (catalog.Permission(requirement.Permission) is null)
            {
                LogUnknown(logger, endpoint!.DisplayName, requirement.Permission);
                await Forbidden(UndeclaredCode, "This operation requires a permission that no module declares.").ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            if (!access.Has(requirement.Permission))
            {
                await Forbidden(ForbiddenCode, "You do not have permission to do this.", requirement.Permission).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        if (metadata.GetMetadata<RecentSignInRequirement>() is { } recent)
        {
            var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
            if (signedInAt is not { } at || now - at > recent.MaxAge)
            {
                await StepUp(context, recent.MaxAge).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static DateTimeOffset? SignedInAt(HttpContext context) =>
        long.TryParse(context.User.FindFirst(AuthTimeClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is > 0 and < 253402300800 // year 10000: anything beyond is not a time
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Forbidden(string code, string title, string? permission = null)
    {
        var problem = Error.Forbidden(code, title).ToProblem();
        if (permission is not null)
        {
            problem.ProblemDetails.Extensions["permission"] = permission;
        }

        return problem;
    }

    /// <summary>401 with the RFC 9470 challenge, so a client knows to re-authenticate (max_age) and retry.</summary>
    private static Task StepUp(HttpContext context, TimeSpan maxAge)
    {
        context.Response.Headers.WWWAuthenticate = Challenge((long)maxAge.TotalSeconds);
        var problem = TypedResults.Problem(
            title: "Confirm your identity to make this change.",
            statusCode: StatusCodes.Status401Unauthorized,
            type: ProblemTypes.For(StepUpCode).ToString(),
            extensions: new Dictionary<string, object?> { ["code"] = StepUpCode, ["maxAge"] = (long)maxAge.TotalSeconds });
        return problem.ExecuteAsync(context);
    }

    /// <summary>The RFC 9470 challenge: the client re-authenticates with <c>max_age</c> and retries.</summary>
    public static string Challenge(long maxAgeSeconds) =>
        $"Bearer error=\"insufficient_user_authentication\", error_description=\"A more recent authentication is required\", max_age={maxAgeSeconds.ToString(CultureInfo.InvariantCulture)}";

    [LoggerMessage(EventId = 1100, Level = LogLevel.Error, Message = "Endpoint {Endpoint} declares no permission; refused")]
    private static partial void LogUndeclared(ILogger logger, string? endpoint);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "Endpoint {Endpoint} requires undeclared permission {Permission}; refused")]
    private static partial void LogUnknown(ILogger logger, string? endpoint, string permission);
}
