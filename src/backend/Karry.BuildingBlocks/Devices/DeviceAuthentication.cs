using System.Buffers.Text;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Karry.BuildingBlocks.Devices;

/// <summary>What the API knows of an enrolled device: its site, its public key (P-256 point) and whether it is revoked.</summary>
public sealed record DeviceKey(Guid DeviceId, Guid SiteId, byte[] X, byte[] Y, bool Revoked);

/// <summary>
/// The enrolled devices of a tenant (implemented by the Identity module). Called under the tenant named by the
/// device's assertion, so row-level security applies.
/// </summary>
public interface IDeviceDirectory
{
    Task<DeviceKey?> FindAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>Records an assertion ID until it expires; false when the device already used it (a replay).</summary>
    Task<bool> RememberAssertionAsync(Guid deviceId, string assertionId, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

/// <summary>
/// Authentication of a field tablet (ADR-0028 §3): <c>Authorization: Device &lt;compact JWS&gt;</c>, ES256 (IEEE
/// P1363 signature, D26) over <c>{tid, iat, exp, jti, htm, htu}</c>, signed by the device's non-extractable key. The
/// expiry is checked against the server's clock (a refused tablet gets the server time to correct its own), the ID
/// is refused a second time, the method and path must be this request's. The principal names the tenant
/// (<c>tenant_id</c>), so tenant resolution, row-level security and the address check work as for people.
/// </summary>
public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Device";

    public const string DeviceIdClaim = "device_id";

    public const string SiteIdClaim = "site_id";

    public const string StatusClaim = "device_status";

    /// <summary>When the tablet signed this request, by its clock (unix seconds): the server measures the tablet's clock offset from it.</summary>
    public const string IssuedAtClaim = "device_iat";

    public const string InvalidCode = "devices.assertion_invalid";

    public const string ClockSkewCode = "devices.clock_skew";

    public const string ReplayedCode = "devices.assertion_replayed";

    /// <summary>The longest an assertion may live, and how far ahead of the server a tablet's clock may run.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan Leeway = TimeSpan.FromMinutes(1);

    private const string FailureKey = "karry.device.failure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemeName + " ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var (principal, failure) = await AuthenticateAsync(header[(SchemeName.Length + 1)..].Trim()).ConfigureAwait(false);
        if (principal is null)
        {
            Context.Items[FailureKey] = failure;
            return AuthenticateResult.Fail(failure);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <summary>401 with the reason; a clock problem also carries the server time, so the tablet can correct its offset.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var code = Context.Items[FailureKey] as string ?? TenantResolutionMiddleware.UnauthenticatedCode;
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (code == ClockSkewCode)
        {
            extensions["serverTime"] = clock.GetUtcNow();
        }

        return TypedResults.Problem(
            title: code == ClockSkewCode ? "The tablet's clock is wrong." : "The tablet could not be authenticated.",
            statusCode: StatusCodes.Status401Unauthorized,
            type: ProblemTypes.For(code).ToString(),
            extensions: extensions).ExecuteAsync(Context);
    }

    private async Task<(ClaimsPrincipal? Principal, string Failure)> AuthenticateAsync(string jws)
    {
        var parts = jws.Split('.');
        if (parts.Length != 3
            || Decode(parts[0]) is not { } headerJson || Decode(parts[1]) is not { } payloadJson || Decode(parts[2]) is not { Length: 64 } signature
            || Read<AssertionHeader>(headerJson) is not { Alg: "ES256", Typ: "karry-device+jwt" } header
            || !Guid.TryParseExact(header.Kid, "D", out var deviceId)
            || Read<AssertionClaims>(payloadJson) is not { } claims
            || !Guid.TryParseExact(claims.Tid, "D", out var tenantValue) || tenantValue == Guid.Empty
            || claims.Jti is not { Length: > 0 and <= 64 })
        {
            return (null, InvalidCode);
        }

        // Bound to this request: an assertion captured for one call cannot be used for another.
        if (!string.Equals(claims.Htm, Request.Method, StringComparison.Ordinal)
            || !string.Equals(claims.Htu, (Request.PathBase + Request.Path).Value, StringComparison.Ordinal))
        {
            return (null, InvalidCode);
        }

        var now = clock.GetUtcNow();
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(claims.Iat);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(claims.Exp);
        if (expiresAt <= issuedAt || expiresAt - issuedAt > MaxLifetime)
        {
            return (null, InvalidCode);
        }

        if (issuedAt > now + Leeway || expiresAt <= now)
        {
            return (null, ClockSkewCode);
        }

        // The device is looked up under the tenant its assertion names (row-level security): a device of another
        // tenant is simply not found.
        var tenant = new TenantId(tenantValue);
        Context.RequestServices.GetRequiredService<ITenantContext>().Set(tenant);
        var directory = Context.RequestServices.GetRequiredService<IDeviceDirectory>();
        if (await directory.FindAsync(deviceId, Context.RequestAborted).ConfigureAwait(false) is not { } device
            || !Verifies(device, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature))
        {
            return (null, InvalidCode);
        }

        if (!await directory.RememberAssertionAsync(deviceId, claims.Jti, expiresAt, Context.RequestAborted).ConfigureAwait(false))
        {
            return (null, ReplayedCode);
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(TenantResolutionMiddleware.TenantClaim, tenant.Value.ToString("D")),
            new Claim("sub", $"device:{deviceId:D}"),
            new Claim(DeviceIdClaim, deviceId.ToString("D")),
            new Claim(SiteIdClaim, device.SiteId.ToString("D")),
            new Claim(StatusClaim, device.Revoked ? "revoked" : "active"),
            new Claim(IssuedAtClaim, claims.Iat.ToString(CultureInfo.InvariantCulture)),
        ], SchemeName, "sub", null));
        return (principal, string.Empty);
    }

    private static bool Verifies(DeviceKey device, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = device.X, Y = device.Y } });
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false; // a stored point not on the curve verifies nothing
        }
    }

    private static byte[]? Decode(string part)
    {
        try
        {
            return Base64Url.DecodeFromChars(part);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static T? Read<T>(byte[] json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };

    private sealed record AssertionHeader(string? Alg, string? Typ, string? Kid);

    private sealed record AssertionClaims(string? Tid, long Iat, long Exp, string? Jti, string? Htm, string? Htu);
}

/// <summary>Endpoint metadata: the caller is an enrolled device; <see cref="AllowRevoked"/> lets a revoked one in (uploads to quarantine).</summary>
public sealed record DeviceEndpoint(bool AllowRevoked);

/// <summary>Who called a device endpoint; <see cref="IssuedAt"/> is when the tablet signed the request, by its own clock.</summary>
public readonly record struct DeviceCaller(Guid DeviceId, Guid SiteId, bool Revoked, DateTimeOffset? IssuedAt = null)
{
    public static DeviceCaller? From(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        DateTimeOffset? issuedAt = long.TryParse(user.FindFirst(DeviceAuthenticationHandler.IssuedAtClaim)?.Value, CultureInfo.InvariantCulture, out var iat)
            ? DateTimeOffset.FromUnixTimeSeconds(iat)
            : null;
        return Guid.TryParse(user.FindFirst(DeviceAuthenticationHandler.DeviceIdClaim)?.Value, CultureInfo.InvariantCulture, out var device)
            && Guid.TryParse(user.FindFirst(DeviceAuthenticationHandler.SiteIdClaim)?.Value, CultureInfo.InvariantCulture, out var site)
                ? new DeviceCaller(device, site, user.FindFirst(DeviceAuthenticationHandler.StatusClaim)?.Value != "active", issuedAt)
                : null;
    }
}
