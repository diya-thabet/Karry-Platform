using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Outbox;
using Karry.Modules.Identity.Application;
using Karry.Modules.Tenancy.Infrastructure;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// User lifecycle end to end (US-P1-IAM-03, US-P1-TEN-01 AC1; TC-P1-IAM-007, TC-P1-IAM-008): provisioning invites the
/// first administrator, invitations are accepted once within 7 days with Keycloak's password policy, the accepted
/// user signs in with Keycloak, and a deactivated user loses API access at once while keeping their name.
/// </summary>
[Trait("Story", "US-P1-IAM-03")]
public sealed class IdentityApiTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private const string Password = "correct-horse-battery-staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Story", "US-P1-TEN-01")]
    public async Task Provisioning_with_an_administrator_invites_them_once_with_a_disabled_mfa_keycloak_account()
    {
        var slug = Slug();
        var email = $"{slug}@example.test";

        var first = await ProvisionAsync(slug, email);
        var again = await ProvisionAsync(slug, email);

        first.GetProperty("adminInvitation").GetProperty("invited").GetBoolean().Should().BeTrue();
        first.GetProperty("adminInvitation").GetProperty("delivered").GetBoolean().Should().BeTrue();
        again.GetProperty("adminInvitation").GetProperty("invited").GetBoolean().Should().BeFalse("provisioning is idempotent");
        var message = host.Sender.Sent.Single(m => m.Destination == email);
        message.Link.ToString().Should().StartWith($"https://{slug}.karry.test/invitation#token=", "the token is in the fragment: never sent to a server");
        message.CompanyName.Should().Be($"Carrière {slug}");

        var account = await AccountAsync(email);
        account.GetProperty("enabled").GetBoolean().Should().BeFalse("usable only after the invitation is accepted");
        account.GetProperty("attributes").GetProperty("tenant_id")[0].GetString().Should().Be(first.GetProperty("id").GetString());
        (await RealmRolesAsync(account.GetProperty("id").GetString()!)).Should().Contain("mfa-required", "tenant admin is privileged");
    }

    [Fact]
    [Trait("Story", "US-P1-TEN-01")]
    public async Task A_provisioned_tenant_is_set_up_in_keycloak_by_the_outbox_subscriber()
    {
        var slug = Slug();
        var tenant = (await ProvisionAsync(slug, $"{slug}@example.test")).GetProperty("id").GetGuid();

        // What Karry.Workers runs continuously (and `dispatch-now` once): Tenancy's events reach Identity's handler.
        await host.Services.GetRequiredService<OutboxDispatcher<TenancyDbContext>>().DispatchPendingAsync(100, Ct);

        using var organizations = await host.Keycloak.AdminAsync(HttpMethod.Get, $"organizations?q=tenant_id:{tenant}", cancellationToken: Ct);
        var found = await organizations.Content.ReadFromJsonAsync<JsonElement>(Ct);
        found.GetArrayLength().Should().Be(1);
        found[0].GetProperty("alias").GetString().Should().Be(slug);
        (await host.Services.GetRequiredService<OutboxDispatcher<TenancyDbContext>>().PendingAsync(Ct)).Should().Be(0, "every handler succeeded");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-008")]
    public async Task An_invitation_is_accepted_once_with_a_password_keycloak_accepts_then_the_user_signs_in()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var email = $"op-{Guid.NewGuid():N}@example.test";
        (await InviteAsync(admin, tenant, email, "site-manager")).StatusCode.Should().Be(HttpStatusCode.Created);
        var token = host.Sender.TokenFor(email);

        var weak = await AcceptAsync(token, "short");
        var accepted = await AcceptAsync(token, Password);
        var reused = await AcceptAsync(token, Password);

        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeAsync(weak)).Should().Be("identity.password_rejected", "Keycloak's password policy decides; the invitation stays valid");
        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        reused.StatusCode.Should().Be(HttpStatusCode.Conflict, "a reused invitation link is rejected");
        (await CodeAsync(reused)).Should().Be("identity.invitation_used");

        using var browser = new KeycloakBrowser(host.Keycloak);
        var final = await browser.LoginAsync(await browser.StartAsync(cancellationToken: Ct), email, Password, Ct);
        var (status, tokens) = await browser.ExchangeAsync(final, Ct);
        status.Should().Be(HttpStatusCode.OK);
        KeycloakBrowser.Claims(tokens.GetProperty("access_token").GetString()!).GetProperty("tenant_id").GetString().Should().Be(tenant.ToString());
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-008")]
    public async Task An_invitation_expires_after_seven_days()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var email = $"late-{Guid.NewGuid():N}@example.test";
        await InviteAsync(admin, tenant, email, "commercial");
        var token = host.Sender.TokenFor(email);

        host.Clock.Advance(TimeSpan.FromDays(7));
        var expired = await AcceptAsync(token, Password);

        expired.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(expired)).Should().Be("identity.invitation_expired");
        (await AccountAsync(email)).GetProperty("enabled").GetBoolean().Should().BeFalse("nothing was activated");
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("0192f0c1a2b37c4d8e9f001122334455.unknown-secret")]
    public async Task Unknown_or_malformed_tokens_get_the_same_answer(string token)
    {
        var response = await AcceptAsync(token, Password);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(response)).Should().Be("identity.invitation_invalid");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-007")]
    public async Task A_deactivated_user_loses_api_access_at_once_and_keycloak_sessions_end_but_the_name_stays()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var email = $"leaver-{Guid.NewGuid():N}@example.test";
        var invited = await InviteAsync(admin, tenant, email, "storekeeper");
        var userId = (await invited.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("userId").GetGuid();
        await AcceptAsync(host.Sender.TokenFor(email), Password);
        using var browser = new KeycloakBrowser(host.Keycloak);
        var (_, tokens) = await browser.ExchangeAsync(await browser.LoginAsync(await browser.StartAsync(cancellationToken: Ct), email, Password, Ct), Ct);
        var leaver = TestTokens.ForTenant(tenant, await SubjectAsync(email));
        (await SendAsync(HttpMethod.Get, "/api/v1/identity/me", leaver)).StatusCode.Should().Be(HttpStatusCode.OK);

        var deactivated = await SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{userId}/deactivate", admin);
        var again = await SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{userId}/deactivate", admin);

        deactivated.StatusCode.Should().Be(HttpStatusCode.NoContent);
        again.StatusCode.Should().Be(HttpStatusCode.NoContent, "idempotent");
        var refused = await SendAsync(HttpMethod.Get, "/api/v1/identity/me", leaver);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the still-valid access token is refused (well within 60 s)");
        (await CodeAsync(refused)).Should().Be("auth.revoked");
        (await browser.RefreshAsync(tokens.GetProperty("refresh_token").GetString()!, Ct)).Status.Should().Be(HttpStatusCode.BadRequest, "Keycloak sessions ended");
        using var later = new KeycloakBrowser(host.Keycloak);
        (await later.LoginAsync(await later.StartAsync(cancellationToken: Ct), email, Password, Ct)).Code.Should().BeNull("the account is disabled");

        var record = await (await SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{userId}", admin)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        record.GetProperty("status").GetString().Should().Be("deactivated");
        record.GetProperty("lastName").GetString().Should().Be("Invitee", "historical records keep the user's name");
    }

    [Fact]
    public async Task An_administrator_cannot_deactivate_themselves_and_others_cannot_manage_users()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var me = await (await SendAsync(HttpMethod.Get, "/api/v1/identity/me", admin)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var stranger = TestTokens.ForTenant(tenant, "not-a-karry-user");

        var self = await SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{me.GetProperty("id").GetGuid()}/deactivate", admin);
        var invite = await InviteAsync(stranger, tenant, "x@example.test", "operator");
        var list = await SendAsync(HttpMethod.Get, "/api/v1/identity/users", stranger);

        self.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(self)).Should().Be("identity.cannot_deactivate_self");
        invite.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        me.GetProperty("roles")[0].GetProperty("role").GetString().Should().Be("tenant-admin");
        me.GetProperty("requiresMfa").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task An_active_user_without_the_tenant_admin_role_cannot_manage_users()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var email = $"manager-{Guid.NewGuid():N}@example.test";
        await InviteAsync(admin, tenant, email, "site-manager");
        await AcceptAsync(host.Sender.TokenFor(email), Password);
        var manager = TestTokens.ForTenant(tenant, await SubjectAsync(email));

        (await InviteAsync(manager, tenant, $"x-{Guid.NewGuid():N}@example.test", "operator")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/identity/users", manager)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/identity/me", manager)).StatusCode.Should().Be(HttpStatusCode.OK, "everyone sees their own profile");
    }

    [Fact]
    public async Task The_same_email_cannot_get_a_second_account_even_in_another_tenant()
    {
        var (tenantA, adminA) = await TenantWithAdminAsync();
        var (tenantB, adminB) = await TenantWithAdminAsync();
        var email = $"shared-{Guid.NewGuid():N}@example.test";
        await InviteAsync(adminA, tenantA, email, "operator");
        await AcceptAsync(host.Sender.TokenFor(email), Password);

        var second = await InviteAsync(adminB, tenantB, email, "operator");

        second.StatusCode.Should().Be(HttpStatusCode.Conflict, "one realm: an e-mail is one Keycloak account (ADR-0012)");
        (await CodeAsync(second)).Should().Be("identity.account_exists");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-03")]
    public async Task Field_users_get_an_employee_id_and_pin_and_the_pin_never_leaves_the_server()
    {
        var (tenant, admin) = await TenantWithAdminAsync();

        var created = await SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", admin,
            new { firstName = "Karim", lastName = "Trabelsi", employeeId = "EMP-042", pin = "804613", roles = new[] { new { role = "weighmaster" } } });
        var duplicate = await SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", admin,
            new { firstName = "Other", lastName = "Person", employeeId = "EMP-042", pin = "804613", roles = new[] { new { role = "operator" } } });
        var list = await (await SendAsync(HttpMethod.Get, "/api/v1/identity/users?limit=200", admin)).Content.ReadAsStringAsync(Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await created.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("\"kind\":\"field\"").And.Contain("EMP-042").And.NotContain("804613").And.NotContain("pbkdf2");
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        list.Should().Contain("EMP-042").And.NotContain("pinHash").And.NotContain("pbkdf2");
    }

    [Fact]
    [Trait("Story", "US-P1-AUD-01")]
    public async Task Identity_changes_are_ledgered_without_personal_data()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var email = $"audit-{Guid.NewGuid():N}@example.test";
        var invited = await InviteAsync(admin, tenant, email, "finance");
        var userId = (await invited.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("userId").GetGuid();
        await SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{userId}/deactivate", admin);

        var entries = await LedgerAsync(tenant);

        entries.Select(e => e.Type).Should().Contain(["identity.user_invited.v1", "identity.user_deactivated.v1"]);
        string.Join('\n', entries.Select(e => e.Payload)).Should().Contain(userId.ToString()).And.NotContain(email).And.NotContain("Invitee");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-01")]
    public async Task Failed_sign_ins_and_the_lockout_reach_the_tenants_ledger_once_without_personal_data()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var email = $"locked-{Guid.NewGuid():N}@example.test";
        await InviteAsync(admin, tenant, email, "weighmaster");
        await AcceptAsync(host.Sender.TokenFor(email), Password);
        using var browser = new KeycloakBrowser(host.Keycloak);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await browser.LoginAsync(await browser.StartAsync(cancellationToken: Ct), email, "wrong-password-123", Ct);
        }

        await browser.LoginAsync(await browser.StartAsync(cancellationToken: Ct), $"nobody-{Guid.NewGuid():N}", "whatever-123", Ct);
        var import = host.Services.GetRequiredService<ImportSignInFailures>();

        var first = await import.RunAsync(Ct);
        var second = await import.RunAsync(Ct);

        first.Imported.Should().BeGreaterThanOrEqualTo(6);
        first.Unattributed.Should().BeGreaterThanOrEqualTo(1, "an unknown username belongs to no tenant");
        second.Imported.Should().Be(0, "each Keycloak event is recorded once");

        // A crash after recording but before saving the cursor: the next run reads everything again.
        await ExecuteAsync("DELETE FROM identity.import_cursors");
        var replay = await import.RunAsync(Ct);
        replay.Imported.Should().Be(0, "Keycloak's event ID is the key: nothing is recorded twice");
        replay.AlreadyImported.Should().BeGreaterThanOrEqualTo(6);
        var subject = await SubjectAsync(email);
        var reasons = await ReasonsAsync(subject);
        reasons.Should().HaveCount(6).And.Contain("invalid_user_credentials").And.Contain("user_temporarily_disabled", "the lockout is audited");
        var ledger = (await LedgerAsync(tenant)).Where(e => e.Type == "identity.sign_in_failed.v1").ToList();
        ledger.Should().HaveCount(6);
        string.Join('\n', ledger.Select(e => e.Payload)).Should().Contain("\"locked\":true").And.NotContain(email).And.NotContain("127.0.0.1").And.NotContain("172.");
    }

    [Fact]
    public async Task Users_are_listed_page_by_page()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        for (var i = 0; i < 3; i++)
        {
            await InviteAsync(admin, tenant, $"page{i}-{Guid.NewGuid():N}@example.test", "operator");
        }

        var first = await (await SendAsync(HttpMethod.Get, "/api/v1/identity/users?limit=2", admin)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var cursor = first.GetProperty("nextCursor").GetGuid();
        var second = await (await SendAsync(HttpMethod.Get, $"/api/v1/identity/users?limit=2&cursor={cursor}", admin)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        first.GetProperty("items").GetArrayLength().Should().Be(2);
        second.GetProperty("items").GetArrayLength().Should().Be(2, "admin + 3 invitees = 4 users");
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        second.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("id").GetGuid())
            .Should().NotIntersectWith(first.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()));
    }

    private static string Slug() => $"id-{Guid.NewGuid():N}"[..20];

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    /// <summary>A new tenant whose first administrator accepted the invitation; returns a token of that administrator.</summary>
    private async Task<(Guid Tenant, string AdminToken)> TenantWithAdminAsync()
    {
        var slug = Slug();
        var email = $"{slug}@example.test";
        var tenant = (await ProvisionAsync(slug, email)).GetProperty("id").GetGuid();
        (await AcceptAsync(host.Sender.TokenFor(email), Password)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return (tenant, TestTokens.ForTenant(tenant, await SubjectAsync(email)));
    }

    private async Task<JsonElement> ProvisionAsync(string slug, string adminEmail)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(),
            new { slug, name = $"Carrière {slug}", admin = new { firstName = "Amel", lastName = "Admin", email = adminEmail } });
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private Task<HttpResponseMessage> InviteAsync(string token, Guid tenant, string email, string role) =>
        SendAsync(HttpMethod.Post, "/api/v1/identity/invitations", token,
            new { firstName = "Test", lastName = "Invitee", email, roles = new[] { new { role, siteId = (Guid?)null } } });

    private Task<HttpResponseMessage> AcceptAsync(string token, string password) =>
        SendAsync(HttpMethod.Post, "/api/v1/identity/invitations/accept", null, new { token, password });

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

    private async Task<string> SubjectAsync(string email)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT subject FROM identity.users WHERE email = $1", connection) { Parameters = { new() { Value = email } } };
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<string>> ReasonsAsync(string subject)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT f.reason FROM identity.sign_in_failures f JOIN identity.users u ON u.id = f.user_id WHERE u.subject = $1 ORDER BY f.occurred_at", connection)
        {
            Parameters = { new() { Value = subject } },
        };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var reasons = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            reasons.Add(reader.GetString(0));
        }

        return reasons;
    }

    private async Task<List<(string Type, string Payload)>> LedgerAsync(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT event_type, payload::text FROM audit.ledger_entries WHERE tenant_id = $1 ORDER BY seq", connection)
        {
            Parameters = { new() { Value = tenant } },
        };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var entries = new List<(string, string)>();
        while (await reader.ReadAsync(Ct))
        {
            entries.Add((reader.GetString(0), reader.GetString(1)));
        }

        return entries;
    }

    private async Task<JsonElement> AccountAsync(string username)
    {
        using var response = await host.Keycloak.AdminAsync(HttpMethod.Get, $"users?username={Uri.EscapeDataString(username)}&exact=true", cancellationToken: Ct);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct))[0];
    }

    private async Task<List<string>> RealmRolesAsync(string userId)
    {
        using var response = await host.Keycloak.AdminAsync(HttpMethod.Get, $"users/{userId}/role-mappings/realm", cancellationToken: Ct);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().Select(r => r.GetProperty("name").GetString()!)];
    }
}
