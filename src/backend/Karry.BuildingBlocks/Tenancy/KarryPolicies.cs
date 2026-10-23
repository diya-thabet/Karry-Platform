using Microsoft.AspNetCore.Authorization;

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

    internal static void Add(AuthorizationOptions options) =>
        options.AddPolicy(PlatformOperator, policy => policy.RequireAuthenticatedUser().RequireClaim(PlatformRoleClaim, OperatorRole));
}
