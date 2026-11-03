using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Devices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Karry.BuildingBlocks.Tenancy;

public static class TenancyExtensions
{
    /// <summary>How far past its expiry (or before its start) a person's token is still accepted.</summary>
    public static readonly TimeSpan TokenClockSkew = TimeSpan.FromSeconds(30);

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
        services.TryAddScoped<SupportCallContext>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddKarryAuthorization();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = configuration["Authentication:Authority"];
            options.Audience = configuration["Authentication:Audience"];
            options.RequireHttpsMetadata = configuration.GetValue("Authentication:RequireHttpsMetadata", true);
            options.MapInboundClaims = false; // keep "sub", "tenant_id" … as issued
            options.TokenValidationParameters.ValidateAudience = !string.IsNullOrEmpty(options.Audience);

            // Hardening plan H-I1. Keycloak and the API are servers with synchronized clocks: a token is accepted at most
            // 30 s past its expiry (the default, 5 minutes, doubled the life of a 5-minute access token), and only with
            // the realm's RS256 signature (no algorithm a forged header could pick, such as "none" or HS256).
            options.TokenValidationParameters.ClockSkew = TokenClockSkew;
            options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
        }).AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(DeviceAuthenticationHandler.SchemeName, null); // field tablets (ADR-0028 §3)
        services.AddAuthorization(KarryPolicies.Add);
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SecondFactorResultHandler>();
        services.Configure<TenancyOptions>(configuration.GetSection(TenancyOptions.Section));
        return services;
    }

    /// <summary>
    /// The address rate limit, authentication, authorization (a tablet is proven here), the caller and company rate
    /// limits, tenant resolution, then the permission check of tenant endpoints (ADR-0022); before idempotency and the
    /// endpoints. The rate limits apply only when the host called <c>AddKarryRateLimits</c>.
    /// </summary>
    public static IApplicationBuilder UseKarryTenancy(this IApplicationBuilder app) =>
        app.UseAddressRateLimit().UseAuthentication().UseAuthorization().UseCallerRateLimit()
            .UseMiddleware<TenantResolutionMiddleware>().UseKarryAuthorization();

    /// <summary>
    /// The endpoint is for enrolled field tablets only (ADR-0028 §3): authenticated by the <c>Device</c> scheme alone, so
    /// a person's token is refused here, and a device's assertion is refused everywhere else. A revoked device is
    /// refused (<c>401 devices.revoked</c>) unless <paramref name="allowRevoked"/> (the upload that quarantines).
    /// </summary>
    public static TBuilder RequireDevice<TBuilder>(this TBuilder builder, bool allowRevoked = false)
        where TBuilder : IEndpointConventionBuilder =>
        builder
            .RequireAuthorization(new AuthorizationPolicyBuilder(DeviceAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser().RequireClaim(DeviceAuthenticationHandler.DeviceIdClaim).Build())
            .WithMetadata(new DeviceEndpoint(allowRevoked));

    /// <summary>Opts an endpoint out of the module default "tenant required" (operator and public endpoints only).</summary>
    public static TBuilder AllowWithoutTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowWithoutTenant());
}
