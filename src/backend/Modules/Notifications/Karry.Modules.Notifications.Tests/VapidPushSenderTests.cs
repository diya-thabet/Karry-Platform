using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Karry.Modules.Notifications.Application;
using Karry.Modules.Notifications.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Notifications.Tests;

/// <summary>
/// The Web Push sender (ADR-0039 §5, RFC 8292): an empty POST signed with VAPID (an ES256 JWT for the push service's
/// origin, 12 hours, the contact) and the outcome of each answer; no keys, no push; only browser push services over
/// HTTPS are accepted as endpoints.
/// </summary>
[Trait("Story", "US-P2-OFF-04")]
public sealed class VapidPushSenderTests : IDisposable
{
    private const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc123";
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FixedClock clock = new(new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero));
    private readonly Recorder push = new();

    public void Dispose() => key.Dispose();

    [Fact]
    public async Task A_push_is_an_empty_post_signed_for_the_push_services_origin()
    {
        using var sender = Sender();

        (await sender.PingAsync(Endpoint, CancellationToken.None)).Should().Be(PushOutcome.Sent);

        var request = push.Requests.Should().ContainSingle().Subject;
        (request.Method, request.Uri, request.Body).Should().Be((HttpMethod.Post, new Uri(Endpoint), 0));
        (request.Ttl, request.Urgency).Should().Be(("86400", "normal"));
        var parts = request.Authorization.Split(", ");
        parts[1].Should().Be($"k={PublicKey()}");
        var jwt = parts[0]["vapid t=".Length..].Split('.');
        Json(jwt[0]).GetProperty("alg").GetString().Should().Be("ES256");
        var claims = Json(jwt[1]);
        claims.GetProperty("aud").GetString().Should().Be("https://fcm.googleapis.com");
        claims.GetProperty("exp").GetInt64().Should().Be(clock.GetUtcNow().AddHours(12).ToUnixTimeSeconds());
        claims.GetProperty("sub").GetString().Should().Be("mailto:support@karry.test");
        key.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), Base64Url.DecodeFromChars(jwt[2]), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation).Should().BeTrue("the push service checks the signature with the public key");
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, PushOutcome.Sent)]
    [InlineData(HttpStatusCode.Gone, PushOutcome.Gone)]
    [InlineData(HttpStatusCode.NotFound, PushOutcome.Gone)]
    [InlineData(HttpStatusCode.TooManyRequests, PushOutcome.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, PushOutcome.Failed)]
    [InlineData(HttpStatusCode.Forbidden, PushOutcome.Failed)]
    public async Task Each_answer_of_the_push_service_has_its_outcome(HttpStatusCode status, PushOutcome outcome)
    {
        push.Status = status;
        using var sender = Sender();

        (await sender.PingAsync(Endpoint, CancellationToken.None)).Should().Be(outcome);
    }

    [Fact]
    public async Task An_unreachable_push_service_is_tried_again_and_no_keys_means_no_push()
    {
        push.Unreachable = true;
        using var sender = Sender();
        using var unconfigured = new VapidPushSender(Options.Create(new WebPushOptions()), clock, NullLogger<VapidPushSender>.Instance, push);

        (await sender.PingAsync(Endpoint, CancellationToken.None)).Should().Be(PushOutcome.Failed);
        (unconfigured.PublicKey, await unconfigured.PingAsync(Endpoint, CancellationToken.None)).Should().Be(((string?)null, PushOutcome.NotConfigured));
    }

    [Fact]
    public void Keys_of_the_wrong_size_are_refused_at_start()
    {
        var wrong = () => new VapidPushSender(Options.Create(new WebPushOptions { PublicKey = Base64Url.EncodeToString(new byte[33]), PrivateKey = PrivateKey() }),
            clock, NullLogger<VapidPushSender>.Instance, push);

        wrong.Should().Throw<InvalidOperationException>().WithMessage("*65 bytes*");
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=abc", true)]
    [InlineData("https://web.push.apple.com/QGz", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc", false)]
    [InlineData("https://user:pw@fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com.evil.test/send", false)]
    [InlineData("https://evilfcm.googleapis.com.test/send", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void Only_a_browsers_push_service_over_https_is_an_endpoint(string endpoint, bool allowed) =>
        PushEndpoints.IsAllowed(endpoint).Should().Be(allowed);

    private VapidPushSender Sender() =>
        new(Options.Create(new WebPushOptions { PublicKey = PublicKey(), PrivateKey = PrivateKey(), Subject = "mailto:support@karry.test" }),
            clock, NullLogger<VapidPushSender>.Instance, push);

    private string PublicKey()
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        return Base64Url.EncodeToString([0x04, .. q.X!, .. q.Y!]);
    }

    private string PrivateKey() => Base64Url.EncodeToString(key.ExportParameters(includePrivateParameters: true).D!);

    private static JsonElement Json(string part) => JsonDocument.Parse(Base64Url.DecodeFromChars(part)).RootElement;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Seen(HttpMethod Method, Uri? Uri, long Body, string Authorization, string Ttl, string Urgency);

    /// <summary>A push service: it answers <see cref="Status"/> and keeps what it was sent.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

        public bool Unreachable { get; set; }

        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable)
            {
                throw new HttpRequestException("No route to the push service.");
            }

            var body = request.Content is null ? 0 : (await request.Content.ReadAsByteArrayAsync(cancellationToken)).Length;
            Requests.Add(new Seen(request.Method, request.RequestUri, body, request.Headers.GetValues("Authorization").Single(),
                request.Headers.GetValues("TTL").Single(), request.Headers.GetValues("Urgency").Single()));
            return new HttpResponseMessage(Status);
        }
    }
}
