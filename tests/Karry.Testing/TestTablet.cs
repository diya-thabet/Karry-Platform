using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.BuildingBlocks.Idempotency;
using Karry.SharedKernel.Canonical;

namespace Karry.Testing;

/// <summary>
/// A field tablet as the field app is (ADR-0028 §2–§3): a P-256 key that signs its enrollment proof and every request
/// (<c>Authorization: Device &lt;compact JWS ES256&gt;</c>), issued by <paramref name="now"/> (the server's clock in tests).
/// </summary>
public sealed class TestTablet(HttpClient client, Func<DateTimeOffset> now) : IDisposable
{
    public const string Devices = "/api/v1/identity/devices";

    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Guid Id { get; private set; }

    public Guid Tenant { get; private set; }

    public Guid Site { get; private set; }

    /// <summary>The per-tablet PIN key the server sent at enrollment (base64).</summary>
    public string PinKey { get; private set; } = string.Empty;

    public async Task<HttpResponseMessage> EnrollAsync(string slug, string code, ECDsa? proofKey = null)
    {
        var point = key.ExportParameters(false).Q;
        var proof = (proofKey ?? key).SignData(Encoding.UTF8.GetBytes($"karry-enroll:{code}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var response = await SendRawAsync(HttpMethod.Post, $"{Devices}/enroll", null, new
        {
            tenant = slug,
            code,
            publicKey = new { kty = "EC", crv = "P-256", x = Base64Url.EncodeToString(point.X), y = Base64Url.EncodeToString(point.Y) },
            proof = Base64Url.EncodeToString(proof),
            name = "Tablette pesage",
            model = "Galaxy Tab A9",
            platform = "Android 14",
            appVersion = "1.0.0",
        }).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
            Id = body.GetProperty("deviceId").GetGuid();
            Tenant = body.GetProperty("tenantId").GetGuid();
            Site = body.GetProperty("siteId").GetGuid();
            PinKey = body.GetProperty("pinKey").GetString()!;
        }

        return response;
    }

    public Task<HttpResponseMessage> UnlockAsync(string employeeId, string pin) =>
        SendAsync(HttpMethod.Post, $"{Devices}/unlock", new { employeeId, pin });

    /// <summary>A device-signed request; the assertion's path is <paramref name="path"/> without its query.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        return SendRawAsync(method, path, Assertion(method.Method, path.Split('?')[0]), body);
    }

    public Task<HttpResponseMessage> SendWithAsync(HttpMethod method, string path, string assertion) => SendRawAsync(method, path, assertion, null);

    /// <summary>A compact JWS ES256 for one request, issued now by the given clock unless told otherwise.</summary>
    public string Assertion(string method, string path, DateTimeOffset? issuedAt = null, TimeSpan? lifetime = null, Guid? tenant = null)
    {
        var iat = issuedAt ?? now();
        var header = Part(new { alg = "ES256", typ = "karry-device+jwt", kid = Id.ToString("D") });
        var payload = Part(new
        {
            tid = (tenant ?? Tenant).ToString("D"),
            iat = iat.ToUnixTimeSeconds(),
            exp = (iat + (lifetime ?? TimeSpan.FromMinutes(5))).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N"),
            htm = method,
            htu = path,
        });
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{payload}.{Base64Url.EncodeToString(signature)}";
    }

    /// <summary>
    /// A command as the field app's outbox holds it (ADR-0028 §6): <c>signature</c> is ES256 over the RFC 8785 form of
    /// the other members. A new UUIDv7 unless <paramref name="commandId"/> is given; device time is now.
    /// </summary>
    public JsonObject Command(long sequence, object payload, Guid? commandId = null, string type = "test.item_recorded.v1")
    {
        var command = new JsonObject
        {
            ["commandId"] = (commandId ?? Guid.CreateVersion7(now())).ToString("D"),
            ["sequence"] = sequence,
            ["type"] = type,
            ["payload"] = JsonSerializer.SerializeToNode(payload),
            ["deviceTime"] = now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        var signature = key.SignData(JsonCanonicalizer.Canonicalize(command.ToJsonString()), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        command["signature"] = Base64Url.EncodeToString(signature);
        return command;
    }

    public void Dispose() => key.Dispose();

    private static string Part(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, string? assertion, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (assertion is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Device", assertion);
        }

        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }
}
