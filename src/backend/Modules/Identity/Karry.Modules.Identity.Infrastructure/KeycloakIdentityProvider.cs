using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.Modules.Identity.Application;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>Connection to the realm admin API (section <c>Identity:Keycloak</c>), as the <c>karry-identity</c> service account.</summary>
public sealed class KeycloakOptions
{
    public const string Section = "Identity:Keycloak";

    /// <summary>Keycloak root, e.g. <c>http://localhost:8080</c>.</summary>
    public Uri? BaseUrl { get; set; }

    public string Realm { get; set; } = "karry";

    public string ClientId { get; set; } = "karry-identity";

    public string? ClientSecret { get; set; }

    /// <summary>The app client whose redirect URIs get each tenant's address (field tablets sign in as devices, ADR-0028).</summary>
    public string PortalClientId { get; set; } = "karry-portal";

    public string MfaRole { get; set; } = "mfa-required";
}

/// <summary>
/// <see cref="IIdentityProvider"/> over the Keycloak admin REST API. The service-account token is cached until shortly
/// before it expires. Client redirect URIs are updated read-modify-write: serialized within the process, and verified
/// and retried against another process's concurrent update (two workers provisioning tenants at the same moment).
/// </summary>
internal sealed class KeycloakIdentityProvider(HttpClient http, IOptions<KeycloakOptions> options, IOptions<IdentityOptions> identity, TimeProvider clock)
    : IIdentityProvider, ISignInEventSource, IDisposable
{
    private const string TenantAttribute = "tenant_id";

    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private readonly SemaphoreSlim clientLock = new(1, 1);
    private string? token;
    private DateTimeOffset tokenExpires;

    private KeycloakOptions Options => options.Value;

    public void Dispose()
    {
        tokenLock.Dispose();
        clientLock.Dispose();
        http.Dispose();
    }

    public async Task<Result<string>> CreateAccountAsync(NewAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var created = await SendAsync(HttpMethod.Post, "users", new
        {
            username = account.Username,
            email = account.Email,
            emailVerified = account.Email is not null,
            firstName = account.FirstName,
            lastName = account.LastName,
            enabled = false,
            attributes = new Dictionary<string, string[]> { [TenantAttribute] = [account.Tenant.ToString()] },
        }, cancellationToken).ConfigureAwait(false);

        string subject;
        if (created.StatusCode == HttpStatusCode.Created)
        {
            subject = created.Headers.Location!.Segments[^1];
        }
        else if (created.StatusCode == HttpStatusCode.Conflict
            && await FindAdoptableAsync(account, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            subject = existing;
        }
        else if (created.StatusCode == HttpStatusCode.Conflict)
        {
            return Error.Conflict("identity.account_exists", "This e-mail or phone number already has a Karry account (possibly in another company).");
        }
        else
        {
            throw await FailureAsync(created, "create user", cancellationToken).ConfigureAwait(false);
        }

        if (account.MfaRequired)
        {
            await SetMfaRequiredAsync(subject, required: true, cancellationToken).ConfigureAwait(false);
        }

        return subject;
    }

    public async Task SetMfaRequiredAsync(string subject, bool required, CancellationToken cancellationToken)
    {
        using var role = await SendAsync(HttpMethod.Get, $"roles/{Options.MfaRole}", null, cancellationToken).ConfigureAwait(false);
        var representation = await Ensure(role, "read role").Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
        // POST adds, DELETE removes the mapping; both succeed when it is already as asked (idempotent). An account
        // removed from Keycloak has nothing to sync (like deactivation).
        using var mapped = await SendAsync(required ? HttpMethod.Post : HttpMethod.Delete, $"users/{subject}/role-mappings/realm",
            new[] { representation }, cancellationToken).ConfigureAwait(false);
        if (mapped.StatusCode != HttpStatusCode.NotFound)
        {
            Ensure(mapped, required ? "map role" : "unmap role");
        }
    }

    public async Task SignOutAsync(string subject, CancellationToken cancellationToken)
    {
        using var logout = await SendAsync(HttpMethod.Post, $"users/{subject}/logout", null, cancellationToken).ConfigureAwait(false);
        if (logout.StatusCode != HttpStatusCode.NotFound)
        {
            Ensure(logout, "end sessions");
        }
    }

    public async Task<Result<string>> ActivateAsync(string subject, string password, CancellationToken cancellationToken)
    {
        using var reset = await SendAsync(HttpMethod.Put, $"users/{subject}/reset-password",
            new { type = "password", value = password, temporary = false }, cancellationToken).ConfigureAwait(false);
        if (reset.StatusCode == HttpStatusCode.BadRequest)
        {
            var problem = await reset.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
            return Error.Validation("identity.password_rejected",
                problem?["error_description"]?.GetValue<string>() ?? "The password does not meet the password policy.");
        }

        Ensure(reset, "set password");
        using var enabled = await SendAsync(HttpMethod.Put, $"users/{subject}", new { enabled = true }, cancellationToken).ConfigureAwait(false);
        Ensure(enabled, "enable user");
        return subject;
    }

    public async Task DisableAndSignOutAsync(string subject, CancellationToken cancellationToken)
    {
        using var disabled = await SendAsync(HttpMethod.Put, $"users/{subject}", new { enabled = false }, cancellationToken).ConfigureAwait(false);
        if (disabled.StatusCode != HttpStatusCode.NotFound)
        {
            Ensure(disabled, "disable user");
            using var logout = await SendAsync(HttpMethod.Post, $"users/{subject}/logout", null, cancellationToken).ConfigureAwait(false);
            Ensure(logout, "end sessions");
        }
    }

    public async Task EnsureTenantAsync(TenantId tenant, string slug, string name, CancellationToken cancellationToken)
    {
        using var found = await SendAsync(HttpMethod.Get, $"organizations?q={TenantAttribute}:{tenant}&briefRepresentation=true", null, cancellationToken).ConfigureAwait(false);
        var organizations = await Ensure(found, "find organization").Content.ReadFromJsonAsync<JsonArray>(cancellationToken).ConfigureAwait(false);
        if (organizations is not { Count: > 0 })
        {
            // Organization names are unique in a realm, company names are not: the slug makes it unique.
            using var created = await SendAsync(HttpMethod.Post, "organizations", new
            {
                name = $"{name} ({slug})",
                alias = slug,
                enabled = true,
                attributes = new Dictionary<string, string[]> { [TenantAttribute] = [tenant.ToString()] },
            }, cancellationToken).ConfigureAwait(false);
            if (created.StatusCode != HttpStatusCode.Conflict)
            {
                Ensure(created, "create organization");
            }
        }

        var settings = identity.Value;
        await AllowOriginAsync(Options.PortalClientId, settings.PortalFor(slug), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KeycloakSignInEvent>> ReadFailuresAsync(DateTimeOffset from, int first, int max, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"events?type=LOGIN_ERROR&dateFrom={from.ToUnixTimeMilliseconds()}&direction=asc&first={first}&max={max}", null, cancellationToken).ConfigureAwait(false);
        var events = await Ensure(response, "read events").Content.ReadFromJsonAsync<JsonArray>(cancellationToken).ConfigureAwait(false) ?? [];
        return [.. events.Where(e => e is not null).Select(e => new KeycloakSignInEvent(
            Guid.Parse(e!["id"]!.GetValue<string>()),
            DateTimeOffset.FromUnixTimeMilliseconds(e["time"]!.GetValue<long>()),
            e["error"]?.GetValue<string>(),
            e["userId"]?.GetValue<string>(),
            e["ipAddress"]?.GetValue<string>()))];
    }

    public async Task<TenantId?> TenantOfAsync(string subject, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"users/{Uri.EscapeDataString(subject)}", null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var user = await Ensure(response, "read user").Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
        var value = user?["attributes"]?[TenantAttribute]?.AsArray().FirstOrDefault()?.GetValue<string>();
        return Guid.TryParseExact(value, "D", out var tenant) && tenant != Guid.Empty ? new TenantId(tenant) : null;
    }

    private static string OriginOf(Uri address) => address.GetLeftPart(UriPartial.Authority);

    private static HttpResponseMessage Ensure(HttpResponseMessage response, string operation) =>
        response.IsSuccessStatusCode ? response : throw new HttpRequestException($"Keycloak {operation} failed: {(int)response.StatusCode}.", null, response.StatusCode);

    private static async Task<HttpRequestException> FailureAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new HttpRequestException($"Keycloak {operation} failed: {(int)response.StatusCode} {body[..Math.Min(body.Length, 200)]}", null, response.StatusCode);
    }

    private static bool Contains(JsonNode? list, string value) =>
        list is JsonArray array && array.Any(item => item?.GetValue<string>() == value);

    /// <summary>A disabled account of the same tenant with this username, left by an invitation that failed after creating it.</summary>
    private async Task<string?> FindAdoptableAsync(NewAccount account, CancellationToken cancellationToken)
    {
        using var found = await SendAsync(HttpMethod.Get, $"users?username={Uri.EscapeDataString(account.Username)}&exact=true", null, cancellationToken).ConfigureAwait(false);
        var users = await Ensure(found, "find user").Content.ReadFromJsonAsync<JsonArray>(cancellationToken).ConfigureAwait(false);
        var user = users?.SingleOrDefault();
        return user is not null && user["enabled"]?.GetValue<bool>() == false && Contains(user["attributes"]?[TenantAttribute], account.Tenant.ToString())
            ? user["id"]!.GetValue<string>()
            : null;
    }

    private async Task AllowOriginAsync(string clientId, Uri address, CancellationToken cancellationToken)
    {
        await clientLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AllowOriginLockedAsync(clientId, address, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            clientLock.Release();
        }
    }

    private async Task AllowOriginLockedAsync(string clientId, Uri address, CancellationToken cancellationToken)
    {
        var origin = OriginOf(address);
        var redirect = $"{origin}/*";
        for (var attempt = 1; ; attempt++)
        {
            using var found = await SendAsync(HttpMethod.Get, $"clients?clientId={Uri.EscapeDataString(clientId)}", null, cancellationToken).ConfigureAwait(false);
            var client = (await Ensure(found, "find client").Content.ReadFromJsonAsync<JsonArray>(cancellationToken).ConfigureAwait(false))?.SingleOrDefault()?.AsObject()
                ?? throw new InvalidOperationException($"Keycloak client {clientId} does not exist.");
            if (Contains(client["redirectUris"], redirect) && Contains(client["webOrigins"], origin))
            {
                return;
            }

            if (attempt > 5)
            {
                throw new InvalidOperationException($"Could not add {origin} to Keycloak client {clientId} (concurrent updates).");
            }

            Append(client, "redirectUris", redirect);
            Append(client, "webOrigins", origin);
            var attributes = client["attributes"] as JsonObject ?? [];
            var logout = attributes["post.logout.redirect.uris"]?.GetValue<string>();
            if (logout is null || !logout.Split("##").Contains(redirect))
            {
                attributes["post.logout.redirect.uris"] = string.IsNullOrEmpty(logout) ? redirect : $"{logout}##{redirect}";
            }

            client["attributes"] = attributes;
            using var updated = await SendAsync(HttpMethod.Put, $"clients/{client["id"]!.GetValue<string>()}", client, cancellationToken).ConfigureAwait(false);
            Ensure(updated, "update client");
        }
    }

    private static void Append(JsonObject client, string property, string value)
    {
        var list = client[property] as JsonArray ?? [];
        if (!Contains(list, value))
        {
            list.Add(value);
        }

        client[property] = list;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var baseUrl = Options.BaseUrl ?? throw new InvalidOperationException($"{KeycloakOptions.Section}:BaseUrl is not configured.");
        using var request = new HttpRequestMessage(method, new Uri(baseUrl, $"admin/realms/{Options.Realm}/{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancellationToken).ConfigureAwait(false));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (token is not null && clock.GetUtcNow() < tokenExpires)
            {
                return token;
            }

            using var response = await http.PostAsync(new Uri(Options.BaseUrl!, $"realms/{Options.Realm}/protocol/openid-connect/token"),
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = Options.ClientId,
                    ["client_secret"] = Options.ClientSecret ?? throw new InvalidOperationException($"{KeycloakOptions.Section}:ClientSecret is not configured."),
                }), cancellationToken).ConfigureAwait(false);
            var body = await Ensure(response, "service-account token").Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
            token = body!["access_token"]!.GetValue<string>();
            tokenExpires = clock.GetUtcNow().AddSeconds(body["expires_in"]!.GetValue<int>() - 30);
            return token;
        }
        finally
        {
            tokenLock.Release();
        }
    }
}
