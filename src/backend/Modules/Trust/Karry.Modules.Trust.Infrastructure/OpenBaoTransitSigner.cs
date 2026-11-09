using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Karry.Modules.Trust.Application;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>
/// Configuration section <c>Trust:ServerSigning</c> (ADR-0033 §3). Server signing is enabled when
/// <see cref="Address"/> is set.
/// </summary>
public sealed class ServerSigningOptions
{
    public const string Section = "Trust:ServerSigning";

    /// <summary>OpenBao's address, e.g. <c>http://127.0.0.1:8200</c>.</summary>
    public string? Address { get; set; }

    /// <summary>
    /// A token allowed to sign with the key and read its public versions (and, with <see cref="CreateKeyIfMissing"/>,
    /// to create it). From a secret store or environment variable, never from a committed file.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Where the transit secrets engine is mounted.</summary>
    public string Mount { get; set; } = "transit";

    /// <summary>The transit key; its versions are the <c>kid</c>s <c>{KeyName}-v{n}</c>.</summary>
    public string KeyName { get; set; } = "karry-server";

    /// <summary>Create the <c>ecdsa-p256</c> key, not exportable, when it does not exist (local stack and CI).</summary>
    public bool CreateKeyIfMissing { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// <see cref="IServerSigner"/> on the OpenBao transit secrets engine, by its HTTP API (no SDK). The digest is signed
/// with <c>prehashed: true</c> and <c>marshaling_algorithm: jws</c>, so OpenBao answers IEEE P1363 directly
/// (verified on OpenBao 2.7.2: 64 bytes, verifiable with the version's public key, old versions still verify after a
/// rotation). The key never leaves OpenBao.
/// </summary>
public sealed class OpenBaoTransitSigner : IServerSigner, IDisposable
{
    private readonly ServerSigningOptions options;
    private readonly HttpClient http;
    private readonly SemaphoreSlim keyReady = new(1, 1);
    private bool keyChecked;

    public OpenBaoTransitSigner(ServerSigningOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Address);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Token);
        this.options = options;
        http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = new Uri(options.Address.TrimEnd('/') + "/v1/"),
            Timeout = options.Timeout,
        };
        http.DefaultRequestHeaders.Add("X-Vault-Token", options.Token);
    }

    public bool Available => true;

    public async Task<ServerSignature> SignAsync(ReadOnlyMemory<byte> sha256Digest, CancellationToken cancellationToken)
    {
        if (sha256Digest.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("A server signature is made over a SHA-256 digest (32 bytes).", nameof(sha256Digest));
        }

        await EnsureKeyAsync(cancellationToken).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["input"] = Convert.ToBase64String(sha256Digest.Span),
            ["prehashed"] = true,
            ["hash_algorithm"] = "sha2-256",
            ["marshaling_algorithm"] = "jws",
        };
        var data = await CallAsync(HttpMethod.Post, $"{options.Mount}/sign/{options.KeyName}", body, cancellationToken).ConfigureAwait(false);

        // "vault:v{n}:{base64url of r || s}"
        var parts = (data?["signature"]?.GetValue<string>() ?? string.Empty).Split(':');
        if (parts.Length != 3 || parts[0] != "vault" || !parts[1].StartsWith('v') || !int.TryParse(parts[1][1..], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new InvalidOperationException("OpenBao answered a signature in an unexpected form.");
        }

        var signature = FromBase64Url(parts[2]);
        if (signature.Length != 64)
        {
            throw new InvalidOperationException("OpenBao answered a signature that is not IEEE P1363 P-256 (64 bytes).");
        }

        return new ServerSignature(KeyIdOf(version), signature);
    }

    public async Task<IReadOnlyList<ServerKey>> KeysAsync(CancellationToken cancellationToken)
    {
        await EnsureKeyAsync(cancellationToken).ConfigureAwait(false);
        var data = await CallAsync(HttpMethod.Get, $"{options.Mount}/keys/{options.KeyName}", null, cancellationToken).ConfigureAwait(false);
        if (data?["type"]?.GetValue<string>() != "ecdsa-p256" || data["keys"] is not JsonObject versions)
        {
            throw new InvalidOperationException($"OpenBao key '{options.KeyName}' is not an ecdsa-p256 transit key.");
        }

        return [.. versions
            .Select(v => new ServerKey(
                KeyIdOf(int.Parse(v.Key, NumberStyles.None, CultureInfo.InvariantCulture)),
                v.Value!["public_key"]!.GetValue<string>(),
                DateTimeOffset.Parse(v.Value["creation_time"]!.GetValue<string>(), CultureInfo.InvariantCulture)))
            .OrderBy(k => k.CreatedAt)];
    }

    public string KeyIdOf(int version) => $"{options.KeyName}-v{version.ToString(CultureInfo.InvariantCulture)}";

    public void Dispose()
    {
        http.Dispose();
        keyReady.Dispose();
    }

    private async Task EnsureKeyAsync(CancellationToken cancellationToken)
    {
        if (!options.CreateKeyIfMissing || keyChecked)
        {
            return;
        }

        await keyReady.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!keyChecked)
            {
                // Creating an existing key changes nothing in OpenBao, so this is safe on every start.
                await CallAsync(HttpMethod.Post, $"{options.Mount}/keys/{options.KeyName}", new JsonObject { ["type"] = "ecdsa-p256", ["exportable"] = false }, cancellationToken).ConfigureAwait(false);
                keyChecked = true;
            }
        }
        finally
        {
            keyReady.Release();
        }
    }

    private async Task<JsonNode?> CallAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ServerSignerUnavailableException("The signing service did not answer.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServerSignerUnavailableException("The signing service did not answer in time.", e);
        }

        using (response)
        {
            // 429: rate limited; 5xx: down or sealed (503). Both pass: retry later.
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new ServerSignerUnavailableException($"The signing service answered {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                // 403 (token), 404 (no key or mount), 400: configuration, not an outage. OpenBao's error text holds no secret.
                var errors = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"OpenBao refused {method} {path}: {(int)response.StatusCode} {errors}");
            }

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return null;
            }

            var answer = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
            return answer?["data"];
        }
    }

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }
}
