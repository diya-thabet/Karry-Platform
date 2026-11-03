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
            // Only the trust changes: the production rules (clock skew, RS256 only, lifetime) stay as the host set them.
            options.Authority = null;
            options.TokenValidationParameters.ValidIssuer = Issuer;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidAudience = Audience;
            options.TokenValidationParameters.IssuerSigningKey = Key;
        });

    public static string ForTenant(Guid tenantId, string subject = "user-1") =>
        Create(subject, (TenantResolutionMiddleware.TenantClaim, tenantId.ToString()));

    /// <summary>A Karry operator signed in with password and one-time password (ADR-0029 §5 requires the second factor).</summary>
    public static string ForPlatformOperator(string subject = "operator-1", params (string Type, string Value)[] claims) =>
        Create(subject, [(KarryPolicies.PlatformRoleClaim, KarryPolicies.OperatorRole), .. SecondFactor, .. claims]);

    /// <summary>The <c>amr</c> claims of a sign-in with password and one-time password.</summary>
    public static readonly (string Type, string Value)[] SecondFactor = [(KarryPolicies.AmrClaim, "pwd"), (KarryPolicies.AmrClaim, "otp")];

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
