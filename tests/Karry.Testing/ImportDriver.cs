using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;

namespace Karry.Testing;

/// <summary>
/// Drives the Excel import of a module test host through the API, as the portal does (ADR-0027): upload, the worker's
/// validation, report, preview and commit. <paramref name="runSteps"/> runs the queued import steps, as the worker does;
/// every call is made as <paramref name="subject"/> of the tenant (a Tenant Admin in the module hosts).
/// </summary>
public sealed class ImportDriver(HttpClient client, Func<Task> runSteps, string subject = "admin-1")
{
    public const string WorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Uploads a file and runs its validation; returns the job's ID.</summary>
    public async Task<Guid> ImportAsync(Guid tenant, string kind, byte[] file, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, $"/api/v1/imports?kind={kind}&fileName={kind}.xlsx", tenant);
        request.Content = new ByteArrayContent(file);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(WorkbookContentType);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(response, HttpStatusCode.Accepted, cancellationToken).ConfigureAwait(false);
        var job = (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false)).GetProperty("id").GetGuid();
        await runSteps().ConfigureAwait(false);
        return job;
    }

    /// <summary>Asks for the commit (the response is returned unread: 200, or the refusal); the worker step is not run.</summary>
    public async Task<HttpResponseMessage> RequestCommitAsync(Guid tenant, Guid job, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, $"/api/v1/imports/{job}/commit", tenant);
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks for the commit and runs it; returns the job afterwards (committed, or validated with the new issues).</summary>
    public async Task<JsonElement> CommitAsync(Guid tenant, Guid job, CancellationToken cancellationToken)
    {
        using var response = await RequestCommitAsync(tenant, job, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        await runSteps().ConfigureAwait(false);
        return await JobAsync(tenant, job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Uploads, validates and commits a file expected to have no error; returns the committed job.</summary>
    public async Task<JsonElement> ImportAndCommitAsync(Guid tenant, string kind, byte[] file, CancellationToken cancellationToken)
    {
        var job = await ImportAsync(tenant, kind, file, cancellationToken).ConfigureAwait(false);
        var done = await CommitAsync(tenant, job, cancellationToken).ConfigureAwait(false);
        return done.GetProperty("status").GetString() == "committed"
            ? done
            : throw new InvalidOperationException($"The {kind} import was not committed: {done}");
    }

    public async Task<JsonElement> JobAsync(Guid tenant, Guid job, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, $"/api/v1/imports/{job}", tenant);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The issues of a severity (first 200), as (row, cell, code).</summary>
    public async Task<IReadOnlyList<(int Row, string? Cell, string? Code)>> IssuesAsync(Guid tenant, Guid job, string severity, CancellationToken cancellationToken)
    {
        var page = await PageAsync($"/api/v1/imports/{job}/issues?severity={severity}&limit=200", tenant, cancellationToken).ConfigureAwait(false);
        return [.. page.Select(i => (i.GetProperty("row").GetInt32(), i.GetProperty("cell").GetString(), i.GetProperty("code").GetString()))];
    }

    /// <summary>The preview (first 200 rows), as (key, action).</summary>
    public async Task<IReadOnlyList<(string? Key, string? Action)>> PreviewAsync(Guid tenant, Guid job, CancellationToken cancellationToken)
    {
        var page = await PageAsync($"/api/v1/imports/{job}/rows?limit=200", tenant, cancellationToken).ConfigureAwait(false);
        return [.. page.Select(r => (r.GetProperty("key").GetString(), r.GetProperty("action").GetString()))];
    }

    private async Task<IReadOnlyList<JsonElement>> PageAsync(string path, Guid tenant, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path, tenant);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(response, HttpStatusCode.OK, cancellationToken).ConfigureAwait(false);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return [.. page.GetProperty("items").EnumerateArray()];
    }

    private HttpRequestMessage Request(HttpMethod method, string path, Guid tenant)
    {
        var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get)
        {
            request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, subject));
        return request;
    }

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: expected {(int)expected}, got {(int)response.StatusCode} " +
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
    }
}
