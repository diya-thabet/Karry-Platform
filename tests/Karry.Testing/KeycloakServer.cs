using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Karry.Testing;

/// <summary>
/// Keycloak with the repository's realm and login theme (<c>deploy/keycloak</c>), imported exactly as the local stack
/// does: the Keycloak in <c>KARRY_TEST_KEYCLOAK</c> when set (e.g. the compose stack, <c>http://127.0.0.1:8080</c>),
/// otherwise a Testcontainers instance of the local-stack image. Tests create their own users with random names, so
/// one server serves a whole test class.
/// </summary>
public sealed class KeycloakServer : IAsyncDisposable
{
    /// <summary>Same image as <c>deploy/docker/compose.yaml</c> (a test keeps the two in step).</summary>
    public const string Image = "quay.io/keycloak/keycloak:26.8.1";

    public const string Realm = "karry";

    /// <summary>Confidential client of the Identity module (realm placeholder default, local and test only).</summary>
    public const string IdentityClientId = "karry-identity";

    public const string IdentityClientSecret = "identity-local-only";

    private const ushort HttpPort = 8080;
    private const ushort ManagementPort = 9000;

    private readonly IContainer? container;
    private readonly HttpClient http = new();

    private KeycloakServer(Uri baseUrl, IContainer? container)
    {
        BaseUrl = baseUrl;
        this.container = container;
    }

    /// <summary>Keycloak root, e.g. <c>http://127.0.0.1:32768/</c>.</summary>
    public Uri BaseUrl { get; }

    /// <summary>Issuer of the Karry realm (<c>…/realms/karry</c>, no trailing slash, as in tokens).</summary>
    public string Issuer => new Uri(BaseUrl, $"realms/{Realm}").ToString();

    public static async Task<KeycloakServer> StartAsync()
    {
        var external = Environment.GetEnvironmentVariable("KARRY_TEST_KEYCLOAK");
        if (!string.IsNullOrWhiteSpace(external))
        {
            return new KeycloakServer(new Uri(external.TrimEnd('/') + "/"), null);
        }

        var keycloak = Path.Combine(PostgresServer.RepositoryRoot(), "deploy", "keycloak");
        var container = new ContainerBuilder(Image)
            .WithCommand("start-dev", "--import-realm")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin-test-only")
            .WithEnvironment("KC_HEALTH_ENABLED", "true")
            .WithResourceMapping(new DirectoryInfo(Path.Combine(keycloak, "realm")), "/opt/keycloak/data/import/")
            .WithResourceMapping(new DirectoryInfo(Path.Combine(keycloak, "themes")), "/opt/keycloak/themes/")
            .WithPortBinding(HttpPort, true)
            .WithPortBinding(ManagementPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(ManagementPort).ForPath("/health/ready")))
            .Build();
        await container.StartAsync().ConfigureAwait(false);
        return new KeycloakServer(new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(HttpPort)}/"), container);
    }

    /// <summary>A token of the Identity module's service account (client credentials), for the admin API.</summary>
    public async Task<string> ServiceTokenAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(new Uri($"{Issuer}/protocol/openid-connect/token"), new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = IdentityClientId,
                ["client_secret"] = IdentityClientSecret,
            }), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>Calls the realm admin API (<c>/admin/realms/karry/…</c>) as the Identity service account.</summary>
    public async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? body = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseUrl, $"admin/realms/{Realm}/{path.TrimStart('/')}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ServiceTokenAsync(cancellationToken).ConfigureAwait(false));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates an enabled user with a password and the given attributes and realm roles; returns its ID.</summary>
    public async Task<string> CreateUserAsync(string username, string password, IReadOnlyDictionary<string, string>? attributes = null,
        IReadOnlyCollection<string>? realmRoles = null, CancellationToken cancellationToken = default)
    {
        using var created = await AdminAsync(HttpMethod.Post, "users", new
        {
            username,
            email = $"{username}@example.test",
            firstName = "Test",
            lastName = "User",
            enabled = true,
            attributes = (attributes ?? new Dictionary<string, string>()).ToDictionary(a => a.Key, a => new[] { a.Value }),
            credentials = new[] { new { type = "password", value = password, temporary = false } },
        }, cancellationToken).ConfigureAwait(false);
        if (created.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"Creating user {username} failed: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
        }

        var id = created.Headers.Location!.Segments[^1];
        foreach (var role in realmRoles ?? [])
        {
            using var found = await AdminAsync(HttpMethod.Get, $"roles/{role}", cancellationToken: cancellationToken).ConfigureAwait(false);
            var representation = await found.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
            using var mapped = await AdminAsync(HttpMethod.Post, $"users/{id}/role-mappings/realm", new[] { representation }, cancellationToken).ConfigureAwait(false);
            mapped.EnsureSuccessStatusCode();
        }

        return id;
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        if (container is not null)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
