using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// Drives the API as members of fresh tenants: users are seeded straight into the database (roles as given, default or
/// custom) and their tokens come from the test issuer, optionally with a recent sign-in (<c>auth_time</c>).
/// </summary>
public sealed class TenantMembers(IdentityHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<Guid> TenantAsync()
    {
        var slug = $"az-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    /// <summary>An active office user with these roles; returns a token for them (signed in long ago: no step-up).</summary>
    public Task<string> MemberAsync(Guid tenant, params (string Role, Guid? Site)[] roles) => MemberAsync(tenant, null, roles);

    /// <summary>As above; <paramref name="signedInAt"/> becomes the token's <c>auth_time</c>.</summary>
    public async Task<string> MemberAsync(Guid tenant, DateTimeOffset? signedInAt, params (string Role, Guid? Site)[] roles)
    {
        var subject = $"member-{Guid.NewGuid():N}";
        await InsertAsync(tenant, "Office", roles, subject, employeeId: null);
        return TokenFor(tenant, subject, signedInAt);
    }

    /// <summary>A token of a person who just typed their password (step-up done).</summary>
    public string Recent(Guid tenant, string subject) => TokenFor(tenant, subject, host.Clock.GetUtcNow());

    public Task<Guid> FieldUserAsync(Guid tenant, params (string Role, Guid? Site)[] roles) =>
        InsertAsync(tenant, "Field", roles, subject: null, employeeId: $"E-{Guid.NewGuid():N}"[..20]);

    public async Task<Guid> InsertAsync(Guid tenant, string kind, (string Role, Guid? Site)[] roles, string? subject, string? employeeId)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO identity.users (id, tenant_id, kind, status, first_name, last_name, subject, employee_id, roles, created_at)
            VALUES ($1, $2, $3, 'Active', 'Seeded', 'Member', $4, $5, $6::jsonb, now())
            """, connection);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(tenant);
        insert.Parameters.AddWithValue(kind);
        insert.Parameters.AddWithValue((object?)subject ?? DBNull.Value);
        insert.Parameters.AddWithValue((object?)employeeId ?? DBNull.Value);
        insert.Parameters.AddWithValue(JsonSerializer.Serialize(roles.Select(r => new { role = r.Role, siteId = r.Site })));
        await insert.ExecuteNonQueryAsync(Ct);
        return id;
    }

    /// <summary>Ledger events of a tenant, oldest first (type, payload).</summary>
    public async Task<List<(string Type, JsonElement Payload)>> LedgerAsync(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT event_type, payload::text FROM audit.ledger_entries WHERE tenant_id = $1 ORDER BY seq", connection);
        command.Parameters.AddWithValue(tenant);
        var events = new List<(string, JsonElement)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            events.Add((reader.GetString(0), JsonDocument.Parse(reader.GetString(1)).RootElement.Clone()));
        }

        return events;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await host.Client.SendAsync(request, Ct);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    public static async Task ExpectForbiddenAsync(HttpResponseMessage response) =>
        await ExpectProblemAsync(response, HttpStatusCode.Forbidden, PermissionMiddleware.ForbiddenCode);

    private static string TokenFor(Guid tenant, string subject, DateTimeOffset? signedInAt) => signedInAt is { } at
        ? TestTokens.Create(subject, (Karry.BuildingBlocks.Tenancy.TenantResolutionMiddleware.TenantClaim, tenant.ToString()),
            (PermissionMiddleware.AuthTimeClaim, at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)))
        : TestTokens.ForTenant(tenant, subject);
}
