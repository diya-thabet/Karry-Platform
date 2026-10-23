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
    /// (the Keycloak realm, wired in P1.8) and <c>Authentication:Audience</c>. Without an authority no token can be
    /// validated, so every protected endpoint answers 401 (fail closed).
    /// </summary>
    public static IServiceCollection AddKarryTenancy(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddScoped<ITenantContext, TenantContext>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = configuration["Authentication:Authority"];
            options.Audience = configuration["Authentication:Audience"];
            options.MapInboundClaims = false; // keep "sub", "tenant_id" … as issued
            options.TokenValidationParameters.ValidateAudience = !string.IsNullOrEmpty(options.Audience);
        });
        services.AddAuthorization();
        return services;
    }

    /// <summary>Authentication, authorization, then tenant resolution; before idempotency and the endpoints.</summary>
    public static IApplicationBuilder UseKarryTenancy(this IApplicationBuilder app) =>
        app.UseAuthentication().UseAuthorization().UseMiddleware<TenantResolutionMiddleware>();

    /// <summary>Opts an endpoint out of the module default "tenant required" (operator and public endpoints only).</summary>
    public static TBuilder AllowWithoutTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowWithoutTenant());
}
