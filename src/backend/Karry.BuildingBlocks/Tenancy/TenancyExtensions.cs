using Karry.BuildingBlocks.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.BuildingBlocks.Tenancy;

public static class TenancyExtensions
{
    /// <summary>
    /// Bearer-token authentication and the tenant context. Tokens are validated against <c>Authentication:Authority</c>
    /// (the Keycloak realm, <c>deploy/keycloak</c>) and <c>Authentication:Audience</c> (<c>karry-api</c>). Without an
    /// authority no token can be validated, so every protected endpoint answers 401 (fail closed). The authority must be
    /// HTTPS unless <c>Authentication:RequireHttpsMetadata</c> is false (local stack only).
    /// </summary>
    public static IServiceCollection AddKarryTenancy(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddScoped<ITenantContext, TenantContext>();
        services.AddKarryAuthorization();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = configuration["Authentication:Authority"];
            options.Audience = configuration["Authentication:Audience"];
            options.RequireHttpsMetadata = configuration.GetValue("Authentication:RequireHttpsMetadata", true);
            options.MapInboundClaims = false; // keep "sub", "tenant_id" … as issued
            options.TokenValidationParameters.ValidateAudience = !string.IsNullOrEmpty(options.Audience);
        });
        services.AddAuthorization(KarryPolicies.Add);
        services.Configure<TenancyOptions>(configuration.GetSection(TenancyOptions.Section));
        return services;
    }

    /// <summary>
    /// Authentication, authorization, tenant resolution, then the permission check of tenant endpoints (ADR-0022);
    /// before idempotency and the endpoints.
    /// </summary>
    public static IApplicationBuilder UseKarryTenancy(this IApplicationBuilder app) =>
        app.UseAuthentication().UseAuthorization().UseMiddleware<TenantResolutionMiddleware>().UseKarryAuthorization();

    /// <summary>Opts an endpoint out of the module default "tenant required" (operator and public endpoints only).</summary>
    public static TBuilder AllowWithoutTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowWithoutTenant());
}
