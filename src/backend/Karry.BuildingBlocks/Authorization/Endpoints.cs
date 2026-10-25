using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Karry.BuildingBlocks.Authorization;

/// <summary>
/// The role assignments of a person in a tenant (US-P1-IAM-06), resolved to permissions and field levels. Implemented
/// by the Identity module; an unknown or deactivated subject has none.
/// </summary>
public interface IAccessResolver
{
    ValueTask<IReadOnlyList<AccessAssignment>> AssignmentsAsync(TenantId tenant, string subject, CancellationToken cancellationToken);
}

/// <summary>
/// The current caller's access (scoped, one per request), set by <see cref="PermissionMiddleware"/> before the
/// endpoint runs. Read it in handlers to filter by scope; it is <see cref="CallerAccess.Nobody"/> until set.
/// </summary>
public interface ICallerAccess
{
    CallerAccess Current { get; }

    /// <summary>Subject (<c>sub</c>) of the caller; null for a request without a user.</summary>
    string? Subject { get; }

    /// <summary>When the caller last typed their credentials (<c>auth_time</c> claim), if the token says.</summary>
    DateTimeOffset? SignedInAt { get; }

    void Set(CallerAccess access, string? subject, DateTimeOffset? signedInAt);
}

internal sealed class CallerAccessHolder : ICallerAccess
{
    private bool set;

    public CallerAccess Current { get; private set; } = CallerAccess.Nobody;

    public string? Subject { get; private set; }

    public DateTimeOffset? SignedInAt { get; private set; }

    public void Set(CallerAccess access, string? subject, DateTimeOffset? signedInAt)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (set)
        {
            throw new InvalidOperationException("The caller's access is set once per request.");
        }

        (Current, Subject, SignedInAt, set) = (access, subject, signedInAt, true);
    }
}

/// <summary>Endpoint metadata: the caller must hold this permission at some scope (several = all of them).</summary>
public sealed record PermissionRequirement(string Permission);

/// <summary>Endpoint metadata: any user of the tenant may call it (own profile, own company); a reviewed choice.</summary>
public sealed class AnyTenantMember;

/// <summary>Endpoint metadata: the caller must have typed their credentials within <see cref="MaxAge"/> (step-up).</summary>
public sealed record RecentSignInRequirement(TimeSpan MaxAge);

public static class AuthorizationEndpointExtensions
{
    /// <summary>Default step-up window: the sign-in must be at most 5 minutes old (US-P1-IAM-05 AC3).</summary>
    public static readonly TimeSpan DefaultStepUpWindow = TimeSpan.FromMinutes(5);

    /// <summary>Only callers holding <paramref name="permission"/> (at any scope; the handler checks the object's scope).</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return builder.WithMetadata(new PermissionRequirement(permission));
    }

    /// <summary>Every user of the tenant. Each use is a security decision: name why in a comment.</summary>
    public static TBuilder AllowAnyTenantMember<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AnyTenantMember());

    /// <summary>
    /// Refuses the call with 401 <c>auth.step_up_required</c> (RFC 9470 <c>insufficient_user_authentication</c>) unless
    /// the caller signed in within <paramref name="maxAge"/>; the app then re-authenticates and retries.
    /// </summary>
    public static TBuilder RequireRecentSignIn<TBuilder>(this TBuilder builder, TimeSpan? maxAge = null)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RecentSignInRequirement(maxAge ?? DefaultStepUpWindow));
}

/// <summary>
/// Step-up for privileged changes decided by a handler (the change itself tells whether it is privileged): the same
/// 401 <c>auth.step_up_required</c> as <see cref="AuthorizationEndpointExtensions.RequireRecentSignIn{TBuilder}"/>;
/// module endpoints add the RFC 9470 challenge header to it.
/// </summary>
public static class StepUp
{
    public static readonly Error Required = Error.Unauthorized(PermissionMiddleware.StepUpCode, "Confirm your identity to make this change.");

    /// <summary>The caller typed their credentials within <paramref name="maxAge"/> (default 5 minutes).</summary>
    public static bool IsRecent(ICallerAccess caller, TimeProvider clock, TimeSpan? maxAge = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(clock);
        return caller.SignedInAt is { } at && clock.GetUtcNow() - at <= (maxAge ?? AuthorizationEndpointExtensions.DefaultStepUpWindow);
    }

    /// <summary>Null when the caller signed in recently enough, else <see cref="Required"/>.</summary>
    public static Error? Check(ICallerAccess caller, TimeProvider clock) => IsRecent(caller, clock) ? null : Required;

    /// <summary>Adds the challenge header (and <c>maxAge</c>) to a step-up problem returned by an endpoint.</summary>
    public static async ValueTask<object?> ChallengeFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var result = await next(context).ConfigureAwait(false);
        if (UnwrapProblem(result) is { } problem && problem.ProblemDetails.Extensions.TryGetValue("code", out var code)
            && code as string == PermissionMiddleware.StepUpCode)
        {
            var seconds = (long)AuthorizationEndpointExtensions.DefaultStepUpWindow.TotalSeconds;
            problem.ProblemDetails.Extensions["maxAge"] = seconds;
            context.HttpContext.Response.Headers.WWWAuthenticate = PermissionMiddleware.Challenge(seconds);
        }

        return result;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? UnwrapProblem(object? result) => result switch
    {
        Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult problem => problem,
        INestedHttpResult { Result: Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult problem } => problem,
        _ => null,
    };
}
