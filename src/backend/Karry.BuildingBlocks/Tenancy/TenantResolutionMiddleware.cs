using System.Diagnostics;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Observability;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Karry.BuildingBlocks.Tenancy;

/// <summary>Endpoint metadata: the endpoint works on one tenant's data (default for every module endpoint).</summary>
public sealed class TenantRequired;

/// <summary>Endpoint metadata: the endpoint works without a tenant (platform operator, public branding lookup).</summary>
public sealed class AllowWithoutTenant;

/// <summary>
/// Resolves the tenant of a request from the <b>validated</b> access token (claim <c>tenant_id</c>) and puts it into
/// the scoped <see cref="ITenantContext"/> (US-P1-TEN-02 AC2). For an endpoint that requires a tenant, a missing or
/// invalid claim rejects the request here, <b>before any query runs</b> (TC-P1-TEN-003). Runs after authentication
/// and authorization, so an anonymous caller already got 401.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string TenantClaim = "tenant_id";

    public const string UnauthenticatedCode = "auth.unauthenticated";

    public const string RevokedCode = "auth.revoked";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        var required = endpoint?.Metadata.GetMetadata<TenantRequired>() is not null
            && endpoint.Metadata.GetMetadata<AllowWithoutTenant>() is null;

        if (TryReadTenant(context, out var tenant))
        {
            if (await HostTenantAsync(context).ConfigureAwait(false) is { } hostTenant && hostTenant != tenant)
            {
                // A token of tenant A used on tenant B's address (stolen session, misrouted link): refuse.
                await Error.Forbidden("tenant.host_mismatch", "This account does not belong to this company's address.")
                    .ToProblem().ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            context.RequestServices.GetRequiredService<ITenantContext>().Set(tenant);
            Activity.Current?.SetTag(TelemetryAttributes.TenantId, tenant.ToString());

            // A deactivated user's token stays cryptographically valid until it expires: refuse it now (TC-P1-IAM-007).
            if (context.RequestServices.GetService<IAccessRevocation>() is { } revocation
                && context.User.FindFirst("sub")?.Value is { Length: > 0 } subject
                && await revocation.IsRevokedAsync(tenant, subject, context.RequestAborted).ConfigureAwait(false))
            {
                await Unauthenticated("This account has been deactivated.", RevokedCode).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }
        else if (required)
        {
            // Normally authorization already answered 401 for anonymous callers; this is the fail-closed backstop.
            var result = context.User.Identity?.IsAuthenticated == true
                ? Error.Forbidden("tenant.missing", "The access token does not name a tenant.").ToProblem()
                : Unauthenticated("Authentication is required.", UnauthenticatedCode);
            await result.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The tenant named by the request address (<c>&lt;slug&gt;.&lt;BaseDomain&gt;</c>), or null when the address is not a
    /// tenant address (other domain, platform name such as <c>api</c>, several labels). A well-formed address of no
    /// company resolves to an impossible tenant, so every token is refused there.
    /// </summary>
    private static async Task<TenantId?> HostTenantAsync(HttpContext context)
    {
        var baseDomain = context.RequestServices.GetService<IOptions<TenancyOptions>>()?.Value.BaseDomain;
        var lookup = context.RequestServices.GetService<ITenantSlugLookup>();
        var host = context.Request.Host.Host;
        if (string.IsNullOrWhiteSpace(baseDomain) || lookup is null
            || !host.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var label = host[..^(baseDomain.Length + 1)];
        if (label.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var resolution = await lookup.ResolveAsync(label.ToLowerInvariant(), context.RequestAborted).ConfigureAwait(false);
        return resolution.IsTenantAddress ? resolution.Tenant ?? new TenantId(Guid.Empty) : null;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Unauthenticated(string title, string code) =>
        TypedResults.Problem(
            title: title,
            statusCode: StatusCodes.Status401Unauthorized,
            type: ProblemTypes.For(code).ToString(),
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool TryReadTenant(HttpContext context, out TenantId tenant)
    {
        tenant = default;
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var claims = context.User.FindAll(TenantClaim).ToList();
        // Exactly one well-formed, non-empty UUID: a token naming two tenants is refused, not resolved to the first.
        if (claims.Count != 1 || !Guid.TryParseExact(claims[0].Value, "D", out var value) || value == Guid.Empty)
        {
            return false;
        }

        tenant = new TenantId(value);
        return true;
    }
}
