using System.Security.Claims;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Flags;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using OpenFeature.Model;

namespace Karry.BuildingBlocks.Flags;

/// <summary>
/// Reads feature flags through OpenFeature (ADR-0019) with the caller's context: tenant (<c>tenantId</c>), user
/// (targeting key: the token subject) and environment. Scoped. A flag that cannot be evaluated falls back to its
/// declared default (OpenFeature never throws at call sites).
/// </summary>
public sealed class FeatureFlagClient(FeatureFlagDomain domain, ITenantContext tenant, IHttpContextAccessor http, IHostEnvironment environment)
{
    public const string TenantAttribute = "tenantId";
    public const string EnvironmentAttribute = "environment";

    public async Task<bool> IsEnabledAsync(FlagDefinition flag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return await OpenFeature.Api.Instance.GetClient(domain.Name).GetBooleanValueAsync(flag.Key, flag.Default, Context(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The caller's evaluation context.</summary>
    public EvaluationContext Context()
    {
        var builder = EvaluationContext.Builder().Set(EnvironmentAttribute, environment.EnvironmentName);
        if (tenant.Current is { } id)
        {
            builder.Set(TenantAttribute, id.Value.ToString("D"));
        }

        if (http.HttpContext?.User.FindFirstValue("sub") is { Length: > 0 } subject)
        {
            builder.SetTargetingKey(subject);
        }

        return builder.Build();
    }
}

/// <summary>
/// The OpenFeature domain of this host's provider. OpenFeature's API is one per process; a domain per host keeps
/// several hosts in one process (tests) from reading each other's flags.
/// </summary>
public sealed record FeatureFlagDomain(string Name);
