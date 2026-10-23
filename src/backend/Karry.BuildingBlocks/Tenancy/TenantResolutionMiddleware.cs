using System.Diagnostics;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Observability;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        var required = endpoint?.Metadata.GetMetadata<TenantRequired>() is not null
            && endpoint.Metadata.GetMetadata<AllowWithoutTenant>() is null;

        if (TryReadTenant(context, out var tenant))
        {
            context.RequestServices.GetRequiredService<ITenantContext>().Set(tenant);
            Activity.Current?.SetTag(TelemetryAttributes.TenantId, tenant.ToString());
        }
        else if (required)
        {
            // Normally authorization already answered 401 for anonymous callers; this is the fail-closed backstop.
            var result = context.User.Identity?.IsAuthenticated == true
                ? Error.Forbidden("tenant.missing", "The access token does not name a tenant.").ToProblem()
                : TypedResults.Problem(
                    title: "Authentication is required.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    type: ProblemTypes.For(UnauthenticatedCode).ToString(),
                    extensions: new Dictionary<string, object?> { ["code"] = UnauthenticatedCode });
            await result.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

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
