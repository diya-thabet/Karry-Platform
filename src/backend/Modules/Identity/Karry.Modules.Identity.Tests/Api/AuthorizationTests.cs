using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.Modules.Identity.Domain;
using Karry.Testing;

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

    private readonly TenantMembers api = new(host);

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
        var tenant = await api.TenantAsync();
        var token = await api.MemberAsync(tenant, (role, null));

        await ExpectAsync(await api.SendAsync(HttpMethod.Get, "/api/v1/identity/users", token), listUsers, HttpStatusCode.OK);
        await ExpectAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", token, FieldUser(Roles.Operator, null)), createUsers, HttpStatusCode.Created);
        await ExpectAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/audit/verify", token), verifyLedger, HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Get, "/api/v1/identity/me", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Get, "/api/v1/tenancy/current", token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_of_no_karry_user_is_denied_everything_but_the_company_name()
    {
        var tenant = await api.TenantAsync();
        var stranger = TestTokens.ForTenant(tenant, "not-a-karry-user");

        await ExpectAsync(await api.SendAsync(HttpMethod.Get, "/api/v1/identity/users", stranger), allowed: false, HttpStatusCode.OK);
        await ExpectAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/audit/verify", stranger), allowed: false, HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Get, "/api/v1/identity/me", stranger)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-006")]
    public async Task A_site_manager_sees_only_the_users_of_their_sites_and_others_are_not_found()
    {
        var tenant = await api.TenantAsync();
        var manager = await api.MemberAsync(tenant, (Roles.SiteManager, SiteA));
        var onA = await api.FieldUserAsync(tenant, (Roles.Operator, SiteA));
        var onB = await api.FieldUserAsync(tenant, (Roles.Operator, SiteB));
        var everywhere = await api.FieldUserAsync(tenant, (Roles.Storekeeper, null));

        var list = await (await api.SendAsync(HttpMethod.Get, "/api/v1/identity/users", manager)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var ids = list.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToList();

        ids.Should().Contain(onA).And.NotContain(onB).And.NotContain(everywhere);
        (await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{onA}", manager)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{onB}", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{everywhere}", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{onB}/deactivate", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_site_scoped_list_pages_like_the_full_list()
    {
        var tenant = await api.TenantAsync();
        var manager = await api.MemberAsync(tenant, (Roles.SiteManager, SiteA));
        for (var i = 0; i < 3; i++)
        {
            await api.FieldUserAsync(tenant, (Roles.Operator, SiteA));
            await api.FieldUserAsync(tenant, (Roles.Operator, SiteB));
        }

        var first = await (await api.SendAsync(HttpMethod.Get, "/api/v1/identity/users?limit=2", manager)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var cursor = first.GetProperty("nextCursor").GetGuid();
        var second = await (await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/users?limit=2&cursor={cursor}", manager)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        first.GetProperty("items").GetArrayLength().Should().Be(2);
        second.GetProperty("items").GetArrayLength().Should().Be(2, "the manager (on A) and the third operator of A");
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_site_manager_manages_users_of_their_sites_only()
    {
        var tenant = await api.TenantAsync();
        var manager = await api.MemberAsync(tenant, (Roles.SiteManager, SiteA));
        var onA = await api.FieldUserAsync(tenant, (Roles.Operator, SiteA));
        var onAandB = await api.FieldUserAsync(tenant, (Roles.Operator, SiteA), (Roles.Operator, SiteB));

        (await api.SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", manager, FieldUser(Roles.Operator, SiteA))).StatusCode.Should().Be(HttpStatusCode.Created);
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", manager, FieldUser(Roles.Operator, SiteB)), HttpStatusCode.Forbidden, "identity.scope_exceeded");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", manager, FieldUser(Roles.Operator, null)), HttpStatusCode.Forbidden, "identity.scope_exceeded");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{onAandB}/deactivate", manager), HttpStatusCode.Forbidden, "identity.scope_exceeded");
        (await api.SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{onA}/deactivate", manager)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-05")]
    public async Task Nobody_gives_a_role_with_privileged_rights_they_do_not_hold()
    {
        var tenant = await api.TenantAsync();
        var manager = await api.MemberAsync(tenant, (Roles.SiteManager, null));
        var admin = await api.MemberAsync(tenant, (Roles.TenantAdmin, null));

        // A company-wide site manager manages users but not roles: making someone Tenant Admin would give that away.
        await TenantMembers.ExpectProblemAsync(await InviteAsync(manager, Roles.TenantAdmin, null), HttpStatusCode.Forbidden, "identity.escalation");
        // The administrator holds everything the role contains; the refusal above was not about the request itself.
        (await InviteAsync(admin, Roles.TenantAdmin, null)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-05")]
    public async Task A_site_manager_can_appoint_a_site_manager_of_their_own_site_only()
    {
        var tenant = await api.TenantAsync();
        var manager = await api.MemberAsync(tenant, (Roles.SiteManager, SiteA));

        await TenantMembers.ExpectProblemAsync(await InviteAsync(manager, Roles.SiteManager, SiteB), HttpStatusCode.Forbidden, "identity.scope_exceeded");
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

        await TenantMembers.ExpectForbiddenAsync(response);
    }

    private Task<HttpResponseMessage> InviteAsync(string token, string role, Guid? site) =>
        api.SendAsync(HttpMethod.Post, "/api/v1/identity/invitations", token,
            new { firstName = "New", lastName = "Colleague", email = $"c-{Guid.NewGuid():N}@example.test", roles = new[] { new { role, siteId = site } } });
}
