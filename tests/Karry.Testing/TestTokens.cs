using System.Security.Claims;
using System.Security.Cryptography;
using Karry.BuildingBlocks.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Karry.Testing;

/// <summary>
/// A test token issuer standing in for Keycloak (until P1.8): hosts under test trust its key via
/// <see cref="TrustTestIssuer"/>; tests mint tokens for a tenant user or a platform operator.
/// </summary>
public static class TestTokens
{
    public const string Issuer = "https://auth.karry.test/realms/karry";
    public const string Audience = "karry-api";

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "karry-test-issuer" };

    /// <summary>Makes the host validate tokens from this issuer (and only these) instead of a real authority.</summary>
    public static IServiceCollection TrustTestIssuer(this IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = null;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = Key,
            };
        });

    public static string ForTenant(Guid tenantId, string subject = "user-1") =>
        Create(subject, (TenantResolutionMiddleware.TenantClaim, tenantId.ToString()));

    public static string ForPlatformOperator(string subject = "operator-1") =>
        Create(subject, (KarryPolicies.PlatformRoleClaim, KarryPolicies.OperatorRole));

    public static string Create(string subject, params (string Type, string Value)[] claims) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Subject = new ClaimsIdentity([new Claim("sub", subject), .. claims.Select(c => new Claim(c.Type, c.Value))]),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });
}
