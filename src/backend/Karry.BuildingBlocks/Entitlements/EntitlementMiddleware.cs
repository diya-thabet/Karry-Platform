using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.Entitlements;

/// <summary>Resolves a tenant's entitlements (implemented by the Billing module; cached ≤ 60 s, ADR-0023).</summary>
public interface IEntitlementResolver
{
    ValueTask<TenantEntitlements> ResolveAsync(TenantId tenant, CancellationToken cancellationToken);
}

/// <summary>Endpoint metadata: the tenant's plan must include <see cref="Feature"/>.</summary>
public sealed record FeatureRequirement(string Feature);

public static class EntitlementEndpointExtensions
{
    /// <summary>
    /// Only tenants whose plan (with add-ons and overrides) includes <paramref name="feature"/>; others get 403
    /// <c>billing.feature_not_in_plan</c>. Checked after the permission (US-P1-BIL-03 AC1). An unknown code fails at
    /// start-up, not at the first call.
    /// </summary>
    public static TBuilder RequiresFeature<TBuilder>(this TBuilder builder, string feature)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!Features.IsKnown(feature))
        {
            throw new ArgumentException($"Unknown feature '{feature}': declare it in Karry.SharedKernel.Entitlements.Features.", nameof(feature));
        }

        return builder.WithMetadata(new FeatureRequirement(feature));
    }
}

/// <summary>
/// The plan check of tenant endpoints (US-P1-BIL-03, ADR-0023). Runs after the permission check, so a caller without
/// the permission learns nothing about the plan. Fails closed: without a resolver a gated endpoint is refused.
/// </summary>
public sealed class EntitlementMiddleware(RequestDelegate next)
{
    public const string FeatureNotInPlanCode = "billing.feature_not_in_plan";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var metadata = context.GetEndpoint()?.Metadata;
        var required = metadata?.GetOrderedMetadata<FeatureRequirement>() ?? [];
        if (required.Count == 0 || metadata!.GetMetadata<AllowWithoutTenant>() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var services = context.RequestServices;
        var entitlements = services.GetService<ITenantContext>()?.Current is { } tenant && services.GetService<IEntitlementResolver>() is { } resolver
            ? await resolver.ResolveAsync(tenant, context.RequestAborted).ConfigureAwait(false)
            : null;
        foreach (var requirement in required)
        {
            if (entitlements is null || !entitlements.Includes(requirement.Feature))
            {
                await Error.Forbidden(FeatureNotInPlanCode, "This feature is not included in your company's plan.")
                    .With("feature", requirement.Feature).ToProblem().ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
