using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Flags;
using Karry.Modules.Notifications.Application;
using Karry.SharedKernel.Flags;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Notifications.Infrastructure;

/// <summary>
/// Web Push (section <c>Notifications:WebPush</c>, ADR-0039 §5): the VAPID key pair (P-256, base64url: the public key
/// as the 65-byte uncompressed point, the private key as its 32-byte scalar) and the contact the push services may
/// write to. Without both keys push is off. The private key is a secret: from the vault, never in a file.
/// </summary>
public sealed class WebPushOptions
{
    public const string Section = "Notifications:WebPush";

    public string? PublicKey { get; set; }

    public string? PrivateKey { get; set; }

    public string Subject { get; set; } = "mailto:support@karry.app";
}

/// <summary>
/// Sends an empty Web Push with a VAPID signature (RFC 8292): an ES256 JWT for the push service's origin, valid 12
/// hours, and the public key. No payload (RFC 8291 encryption is not needed for "something needs a look"). Redirects
/// are not followed; the endpoint is never logged, only the status code of a refusal.
/// </summary>
public sealed partial class VapidPushSender : IPushSender, IDisposable
{
    private readonly WebPushOptions settings;
    private readonly TimeProvider clock;
    private readonly ILogger<VapidPushSender> logger;
    private readonly HttpClient http;
    private readonly ECDsa? key;

    public VapidPushSender(IOptions<WebPushOptions> options, TimeProvider clock, ILogger<VapidPushSender> logger, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        settings = options.Value;
        this.clock = clock;
        this.logger = logger;
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        key = Key(settings);
    }

    public string? PublicKey => key is null ? null : settings.PublicKey;

    public async Task<PushOutcome> PingAsync(string endpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        if (key is null)
        {
            return PushOutcome.NotConfigured;
        }

        var target = new Uri(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new ByteArrayContent([]) };
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={Token(target)}, k={settings.PublicKey}");
        request.Headers.TryAddWithoutValidation("TTL", "86400");
        request.Headers.TryAddWithoutValidation("Urgency", "normal");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return PushOutcome.Sent;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return PushOutcome.Gone;
            }

            LogRefused(logger, (int)response.StatusCode);
            return PushOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, ex.GetType().Name);
            return PushOutcome.Failed;
        }
    }

    public void Dispose()
    {
        http.Dispose();
        key?.Dispose();
    }

    /// <summary>The VAPID JWT: <c>aud</c> the push service's origin, <c>exp</c> in 12 hours, <c>sub</c> the contact.</summary>
    internal string Token(Uri endpoint)
    {
        var header = Encode("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = clock.GetUtcNow().AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = settings.Subject,
        }));
        var signed = $"{header}.{claims}";
        var signature = key!.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signed}.{Encode(signature)}";
    }

    private static ECDsa? Key(WebPushOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PublicKey) || string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            return null;
        }

        var point = Base64Url.DecodeFromChars(settings.PublicKey);
        var scalar = Base64Url.DecodeFromChars(settings.PrivateKey);
        if (point is not [0x04, ..] || point.Length != 65 || scalar.Length != 32)
        {
            throw new InvalidOperationException("Notifications:WebPush keys must be a P-256 public point (65 bytes) and private scalar (32 bytes), base64url.");
        }

        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
            D = scalar,
        });
    }

    private static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    [LoggerMessage(EventId = 7604, Level = LogLevel.Warning, Message = "A push service refused a push with status {Status}; it will be tried again")]
    private static partial void LogRefused(ILogger logger, int status);

    [LoggerMessage(EventId = 7605, Level = LogLevel.Warning, Message = "A push service could not be reached ({Error}); it will be tried again")]
    private static partial void LogUnreachable(ILogger logger, string error);
}

/// <summary>Push is on for the current company when it is configured and its flag <c>notifications.web-push</c> is on (ADR-0019).</summary>
internal sealed class PushSwitch(IPushSender sender, IServiceProvider services) : IPushSwitch
{
    public async Task<bool> OnAsync(CancellationToken cancellationToken) =>
        sender.PublicKey is not null
        && services.GetService<FeatureFlagClient>() is { } flags
        && await flags.IsEnabledAsync(FeatureFlags.NotificationsWebPush, cancellationToken).ConfigureAwait(false);
}
