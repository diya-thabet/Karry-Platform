using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace Karry.Testing;

/// <summary>A Keycloak login page or the redirect that ended the login.</summary>
public sealed record LoginStep(HttpStatusCode Status, string Html, Uri? Location)
{
    /// <summary>The authorization code when Keycloak redirected back to the app.</summary>
    public string? Code => Location is { } location ? HttpUtility.ParseQueryString(location.Query)["code"] : null;

    /// <summary>The value of a form input of the page (hidden fields carry, e.g., the TOTP secret on the enrolment page).</summary>
    public string? Input(string name)
    {
        var match = Regex.Match(Html, $"<input[^>]*?name=\"{Regex.Escape(name)}\"[^>]*?value=\"([^\"]*)\"", RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    public bool HasInput(string name) => Html.Contains($"name=\"{name}\"", StringComparison.Ordinal);

    /// <summary>The first link of the page whose address contains <paramref name="part"/> (e.g. "reset-credentials").</summary>
    public Uri? Link(string part)
    {
        var match = Regex.Match(Html, $"href=\"([^\"]*{Regex.Escape(part)}[^\"]*)\"", RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var link = WebUtility.HtmlDecode(match.Groups[1].Value);
        return link.StartsWith('/') ? new Uri(link, UriKind.Relative) : new Uri(link);
    }

    internal Uri FormAction() =>
        new(WebUtility.HtmlDecode(Regex.Match(Html, "<form[^>]*?action=\"([^\"]+)\"", RegexOptions.Singleline).Groups[1].Value));
}

/// <summary>
/// Drives Keycloak's login pages over HTTP like a browser would (no JavaScript): Authorization Code + PKCE (S256) for
/// an app client, form posts, required actions, then the code exchange. Cookies are kept by hand because Keycloak
/// marks them <c>Secure</c> even on loopback HTTP, which <see cref="CookieContainer"/> would not send back.
/// </summary>
public sealed partial class KeycloakBrowser : IDisposable
{
    private readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly KeycloakServer server;
    private readonly string clientId;
    private readonly Uri redirectUri;
    private string verifier = string.Empty;

    public KeycloakBrowser(KeycloakServer server, string clientId = "karry-portal", string appOrigin = "http://localhost:5173")
    {
        ArgumentNullException.ThrowIfNull(server);
        this.server = server;
        this.clientId = clientId;
        redirectUri = new Uri($"{appOrigin}/auth/callback");
    }

    /// <summary>Opens the authorization endpoint (new PKCE pair); <paramref name="extra"/> overrides or adds query parameters.</summary>
    public async Task<LoginStep> StartAsync(IReadOnlyDictionary<string, string?>? extra = null, CancellationToken cancellationToken = default)
    {
        verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["redirect_uri"] = redirectUri.ToString(),
            ["state"] = Base64Url(RandomNumberGenerator.GetBytes(16)),
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            query[key] = value;
        }

        var url = $"{server.Issuer}/protocol/openid-connect/auth?" +
            string.Join('&', query.Where(q => q.Value is not null).Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value!)}"));
        return await GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Submits the page's form with its hidden fields plus <paramref name="fields"/>.</summary>
    public async Task<LoginStep> SubmitAsync(LoginStep page, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var form = HiddenInputs().Matches(page.Html).ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
        foreach (var (key, value) in fields)
        {
            form[key] = value;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, page.FormAction()) { Content = new FormUrlEncodedContent(form) };
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<LoginStep> LoginAsync(LoginStep page, string username, string password, CancellationToken cancellationToken = default) =>
        SubmitAsync(page, new Dictionary<string, string> { ["username"] = username, ["password"] = password, ["credentialId"] = string.Empty }, cancellationToken);

    /// <summary>Follows a redirect inside Keycloak (e.g. to a required action); not the final redirect to the app.</summary>
    public Task<LoginStep> FollowAsync(LoginStep step, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        return GetAsync(step.Location ?? throw new InvalidOperationException("Not a redirect."), cancellationToken);
    }

    /// <summary>Opens an address in this browser (its cookies), e.g. a link of a page or of an e-mail.</summary>
    public Task<LoginStep> OpenAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        return GetAsync(url.IsAbsoluteUri ? url : new Uri(server.BaseUrl, url), cancellationToken);
    }

    /// <summary>Exchanges the code of the final redirect (with the PKCE verifier) for tokens.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> ExchangeAsync(LoginStep final, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(final);
        return await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = final.Code ?? throw new InvalidOperationException($"No code in {final.Status} {final.Location}."),
            ["redirect_uri"] = redirectUri.ToString(),
            ["code_verifier"] = verifier,
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        TokenAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refreshToken }, cancellationToken);

    /// <summary>Any request to the token endpoint as this public client.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(new Uri($"{server.Issuer}/protocol/openid-connect/token"), new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The claims of a JWT (payload only; signature checks are the API's job).</summary>
    public static JsonElement Claims(string jwt)
    {
        ArgumentNullException.ThrowIfNull(jwt);
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '='))).RootElement.Clone();
    }

    /// <summary>RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 s) over Keycloak's raw secret, as an authenticator app computes it.</summary>
    public static string Totp(string secret, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var counter = BitConverter.GetBytes(at.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

#pragma warning disable CA5350 // HMAC-SHA1 is what RFC 6238 and the realm's OTP policy specify
        var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose() => http.Dispose();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("<input[^>]*?type=\"hidden\"[^>]*?name=\"([^\"]+)\"[^>]*?value=\"([^\"]*)\"", RegexOptions.Singleline)]
    private static partial Regex HiddenInputs();

    private async Task<LoginStep> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LoginStep> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        foreach (var header in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
        {
            var pair = header.Split(';', 2)[0].Split('=', 2);
            if (pair.Length == 2 && pair[1].Length > 0)
            {
                cookies[pair[0]] = pair[1];
            }
            else
            {
                cookies.Remove(pair[0]);
            }
        }

        var location = response.Headers.Location is { } l ? (l.IsAbsoluteUri ? l : new Uri(request.RequestUri!, l)) : null;
        return new LoginStep(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), location);
    }
}
