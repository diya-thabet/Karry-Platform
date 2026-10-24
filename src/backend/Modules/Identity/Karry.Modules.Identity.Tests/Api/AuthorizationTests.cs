using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.Modules.Identity.Domain;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// Permissions and scopes on the real pipeline (US-P1-IAM-04/06, ADR-0022): the allow/deny matrix of the default roles
/// on sample endpoints, users filtered by site scope with 404 outside it (TC-P1-IAM-006 on users), and the
/// anti-escalation rules of threat T-13. Users are seeded straight into the database; tokens come from the test issuer.
/// </summary>
[Trait("Story", "US-P1-IAM-06")]
public sealed class AuthorizationTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private static readonly Guid SiteA = Guid.Parse("01970000-0000-7000-8000-0000000000a1");
    private static readonly Guid SiteB = Guid.Parse("01970000-0000-7000-8000-0000000000b1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Role → may (list users, create a field user, verify the ledger). Every member reads /me and /current.</summary>
    public static TheoryData<string, bool, bool, bool> Matrix => new()
    {
        { Roles.TenantAdmin, true, true, true },
        { Roles.Executive, true, false, true },
        { Roles.Finance, false, false, true },
        { Roles.Controller, false, false, true },
        { Roles.SiteManager, true, true, true },
        { Roles.MaintenancePlanner, false, false, false },
        { Roles.Commercial, false, false, false },
        { Roles.Storekeeper, false, false, false },
        { Roles.Weighmaster, false, false, false },
        { Roles.FuelAttendant, false, false, false },
        { Roles.Mechanic, false, false, false },
        { Roles.Operator, false, false, false },
    };

    [Theory]
    [Trait("Story", "US-P1-IAM-04")]
    [MemberData(nameof(Matrix))]
    public async Task Each_default_role_is_allowed_and_denied_as_the_role_matrix_says(string role, bool listUsers, bool createUsers, bool verifyLedger)
    {
        var tenant = await TenantAsync();
        var token = await MemberAsync(tenant, (role, null));

        await ExpectAsync(await SendAsync(HttpMethod.Get, "/api/v1/identity/users", token), listUsers, HttpStatusCode.OK);
        await ExpectAsync(await SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", token, FieldUser(Roles.Operator, null)), createUsers, HttpStatusCode.Created);
        await ExpectAsync(await SendAsync(HttpMethod.Post, "/api/v1/audit/verify", token), verifyLedger, HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/api/v1/identity/me", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/api/v1/tenancy/current", token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_of_no_karry_user_is_denied_everything_but_the_company_name()
    {
        var tenant = await TenantAsync();
        var stranger = TestTokens.ForTenant(tenant, "not-a-karry-user");

        await ExpectAsync(await SendAsync(HttpMethod.Get, "/api/v1/identity/users", stranger), allowed: false, HttpStatusCode.OK);
        await ExpectAsync(await SendAsync(HttpMethod.Post, "/api/v1/audit/verify", stranger), allowed: false, HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/api/v1/identity/me", stranger)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-006")]
    public async Task A_site_manager_sees_only_the_users_of_their_sites_and_others_are_not_found()
    {
        var tenant = await TenantAsync();
        var manager = await MemberAsync(tenant, (Roles.SiteManager, SiteA));
        var onA = await FieldUserAsync(tenant, (Roles.Operator, SiteA));
        var onB = await FieldUserAsync(tenant, (Roles.Operator, SiteB));
        var everywhere = await FieldUserAsync(tenant, (Roles.Storekeeper, null));

        var list = await (await SendAsync(HttpMethod.Get, "/api/v1/identity/users", manager)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var ids = list.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToList();

        ids.Should().Contain(onA).And.NotContain(onB).And.NotContain(everywhere);
        (await SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{onA}", manager)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{onB}", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{everywhere}", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{onB}/deactivate", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_site_scoped_list_pages_like_the_full_list()
    {
        var tenant = await TenantAsync();
        var manager = await MemberAsync(tenant, (Roles.SiteManager, SiteA));
        for (var i = 0; i < 3; i++)
        {
            await FieldUserAsync(tenant, (Roles.Operator, SiteA));
            await FieldUserAsync(tenant, (Roles.Operator, SiteB));
        }

        var first = await (await SendAsync(HttpMethod.Get, "/api/v1/identity/users?limit=2", manager)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var cursor = first.GetProperty("nextCursor").GetGuid();
        var second = await (await SendAsync(HttpMethod.Get, $"/api/v1/identity/users?limit=2&cursor={cursor}", manager)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        first.GetProperty("items").GetArrayLength().Should().Be(2);
        second.GetProperty("items").GetArrayLength().Should().Be(2, "the manager (on A) and the third operator of A");
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_site_manager_manages_users_of_their_sites_only()
    {
        var tenant = await TenantAsync();
        var manager = await MemberAsync(tenant, (Roles.SiteManager, SiteA));
        var onA = await FieldUserAsync(tenant, (Roles.Operator, SiteA));
        var onAandB = await FieldUserAsync(tenant, (Roles.Operator, SiteA), (Roles.Operator, SiteB));

        (await SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", manager, FieldUser(Roles.Operator, SiteA))).StatusCode.Should().Be(HttpStatusCode.Created);
        await ExpectProblemAsync(await SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", manager, FieldUser(Roles.Operator, SiteB)), HttpStatusCode.Forbidden, "identity.scope_exceeded");
        await ExpectProblemAsync(await SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", manager, FieldUser(Roles.Operator, null)), HttpStatusCode.Forbidden, "identity.scope_exceeded");
        await ExpectProblemAsync(await SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{onAandB}/deactivate", manager), HttpStatusCode.Forbidden, "identity.scope_exceeded");
        (await SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{onA}/deactivate", manager)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-05")]
    public async Task Nobody_gives_a_role_with_privileged_rights_they_do_not_hold()
    {
        var tenant = await TenantAsync();
        var manager = await MemberAsync(tenant, (Roles.SiteManager, null));
        var admin = await MemberAsync(tenant, (Roles.TenantAdmin, null));

        // A company-wide site manager manages users but not roles: making someone Tenant Admin would give that away.
        await ExpectProblemAsync(await InviteAsync(manager, Roles.TenantAdmin, null), HttpStatusCode.Forbidden, "identity.escalation");
        // The administrator holds everything the role contains; the refusal above was not about the request itself.
        (await InviteAsync(admin, Roles.TenantAdmin, null)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-05")]
    public async Task A_site_manager_can_appoint_a_site_manager_of_their_own_site_only()
    {
        var tenant = await TenantAsync();
        var manager = await MemberAsync(tenant, (Roles.SiteManager, SiteA));

        await ExpectProblemAsync(await InviteAsync(manager, Roles.SiteManager, SiteB), HttpStatusCode.Forbidden, "identity.scope_exceeded");
        (await InviteAsync(manager, Roles.SiteManager, SiteA)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static object FieldUser(string role, Guid? site) => new
    {
        firstName = "Field",
        lastName = "Worker",
        employeeId = $"E-{Guid.NewGuid():N}"[..20],
        pin = "730194",
        roles = new[] { new { role, siteId = site } },
    };

    private static async Task ExpectAsync(HttpResponseMessage response, bool allowed, HttpStatusCode success)
    {
        if (allowed)
        {
            response.StatusCode.Should().Be(success, await response.Content.ReadAsStringAsync(Ct));
            return;
        }

        await ExpectProblemAsync(response, HttpStatusCode.Forbidden, PermissionMiddleware.ForbiddenCode);
    }

    private static async Task ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    private Task<HttpResponseMessage> InviteAsync(string token, string role, Guid? site) =>
        SendAsync(HttpMethod.Post, "/api/v1/identity/invitations", token,
            new { firstName = "New", lastName = "Colleague", email = $"c-{Guid.NewGuid():N}@example.test", roles = new[] { new { role, siteId = site } } });

    private async Task<Guid> TenantAsync()
    {
        var slug = $"az-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    /// <summary>An active office user with these roles; returns a token for them.</summary>
    private async Task<string> MemberAsync(Guid tenant, params (string Role, Guid? Site)[] roles)
    {
        var subject = $"member-{Guid.NewGuid():N}";
        await InsertAsync(tenant, "Office", roles, subject, employeeId: null);
        return TestTokens.ForTenant(tenant, subject);
    }

    private Task<Guid> FieldUserAsync(Guid tenant, params (string Role, Guid? Site)[] roles) =>
        InsertAsync(tenant, "Field", roles, subject: null, employeeId: $"E-{Guid.NewGuid():N}"[..20]);

    private async Task<Guid> InsertAsync(Guid tenant, string kind, (string Role, Guid? Site)[] roles, string? subject, string? employeeId)
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null)
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
}
