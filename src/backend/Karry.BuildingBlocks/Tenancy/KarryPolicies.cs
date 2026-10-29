using System.Security.Claims;
using Karry.BuildingBlocks.Api;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Karry.BuildingBlocks.Tenancy;

/// <summary>
/// Authorization policies shared by modules. Provisional until roles and permissions arrive (P1.8/P1.9): the claim
/// names below are what the Keycloak mappers will emit.
/// </summary>
public static class KarryPolicies
{
    /// <summary>Karry staff operating the platform (tenant provisioning, operator console).</summary>
    public const string PlatformOperator = "platform-operator";

    public const string PlatformRoleClaim = "platform_role";

    public const string OperatorRole = "operator";

    /// <summary>How the person signed in (RFC 8176), from Keycloak's AMR mapper: one value per factor used.</summary>
    public const string AmrClaim = "amr";

    /// <summary>An operator token without a second factor (ADR-0029 §5).</summary>
    public const string MfaRequiredCode = "auth.mfa_required";

    /// <summary>The second factors Karry accepts: a one-time password, or a hardware key (WebAuthn).</summary>
    private static readonly string[] SecondFactors = ["otp", "hwk"];

    /// <summary>True when the sign-in used a second factor (MFA at every operator login is also enforced by Keycloak).</summary>
    public static bool HasSecondFactor(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindAll(AmrClaim).Any(claim => SecondFactors.Contains(claim.Value, StringComparer.Ordinal));
    }

    /// <summary>The 401 an operator gets without a second factor: sign in again with it.</summary>
    public static IResult MfaRequired() =>
        Error.Unauthorized(MfaRequiredCode, "Karry operators must sign in with a second factor. Sign out, then sign in again with it.").ToProblem();

    internal static void Add(AuthorizationOptions options) =>
        options.AddPolicy(PlatformOperator, policy => policy.RequireAuthenticatedUser()
            .RequireClaim(PlatformRoleClaim, OperatorRole)
            .AddRequirements(new SecondFactorRequirement()));
}

/// <summary>The sign-in used a second factor (<see cref="KarryPolicies.HasSecondFactor"/>); handles itself.</summary>
public sealed class SecondFactorRequirement : AuthorizationHandler<SecondFactorRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SecondFactorRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (KarryPolicies.HasSecondFactor(context.User))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// An operator refused only for the missing second factor gets 401 <c>auth.mfa_required</c> (sign in again), not the
/// generic 403: everything else is answered as the framework does.
/// </summary>
internal sealed class SecondFactorResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        var failed = authorizeResult.AuthorizationFailure?.FailedRequirements.ToList() ?? [];
        return authorizeResult.Forbidden && failed.Count > 0 && failed.All(requirement => requirement is SecondFactorRequirement)
            ? KarryPolicies.MfaRequired().ExecuteAsync(context)
            : fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
