using System.Net;
using Karry.BuildingBlocks.Authorization;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// Changing a user's roles (US-P1-IAM-06 AC1, threat T-13) and resending invitations (US-P1-IAM-03) on the real
/// pipeline with a real Keycloak: nobody changes their own roles, the grant rules and step-up apply, the change is in
/// the ledger and takes effect at once, and Keycloak's <c>mfa-required</c> follows the roles.
/// </summary>
[Trait("Story", "US-P1-IAM-06")]
public sealed class UserRoleTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private static readonly Guid SiteA = Guid.Parse("01970000-0000-7000-8000-0000000000a1");
    private static readonly Guid SiteB = Guid.Parse("01970000-0000-7000-8000-0000000000b1");

    private readonly TenantMembers api = new(host);

    [Fact]
    public async Task A_role_change_takes_effect_at_once_and_is_in_the_ledger_with_before_and_after()
    {
        var (tenant, admin, _) = await AdminAsync(recent: true);
        var subject = $"member-{Guid.NewGuid():N}";
        var id = await api.InsertAsync(tenant, "Office", [(Roles.Commercial, null)], subject, employeeId: null);
        var member = Karry.Testing.TestTokens.ForTenant(tenant, subject);
        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Get, "/api/v1/identity/users", member));

        var changed = await TenantMembers.JsonAsync(await ChangeAsync(admin, id, (Roles.SiteManager, SiteA)), HttpStatusCode.OK);

        changed.GetProperty("roles")[0].GetProperty("role").GetString().Should().Be(Roles.SiteManager);
        (await api.SendAsync(HttpMethod.Get, "/api/v1/identity/users", member)).StatusCode.Should().Be(HttpStatusCode.OK, "no 30 s wait on this instance");
        var entry = (await api.LedgerAsync(tenant)).Single(e => e.Type == "identity.user_roles_changed.v1").Payload;
        entry.GetProperty("before")[0].GetProperty("role").GetString().Should().Be(Roles.Commercial);
        entry.GetProperty("after")[0].GetProperty("siteId").GetGuid().Should().Be(SiteA);
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-05")]
    public async Task Nobody_changes_their_own_roles()
    {
        var tenant = await api.TenantAsync();
        var subject = $"admin-{Guid.NewGuid():N}";
        var id = await api.InsertAsync(tenant, "Office", [(Roles.TenantAdmin, null)], subject, employeeId: null);

        await TenantMembers.ExpectProblemAsync(await ChangeAsync(api.Recent(tenant, subject), id, (Roles.Executive, null)),
            HttpStatusCode.UnprocessableEntity, "identity.cannot_change_own_roles");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-009")]
    public async Task Giving_or_taking_a_privileged_role_needs_a_recent_sign_in_and_other_changes_do_not()
    {
        var (tenant, admin, subject) = await AdminAsync(recent: false);
        var id = await api.FieldUserAsync(tenant, (Roles.Operator, null));
        var officeId = await api.InsertAsync(tenant, "Office", [(Roles.Commercial, null)], $"m-{Guid.NewGuid():N}", employeeId: null);

        (await ChangeAsync(admin, id, (Roles.Mechanic, null))).StatusCode.Should().Be(HttpStatusCode.OK, "operator → mechanic touches no privileged right");
        var refused = await ChangeAsync(admin, officeId, (Roles.Finance, null));
        await TenantMembers.ExpectProblemAsync(refused, HttpStatusCode.Unauthorized, PermissionMiddleware.StepUpCode);
        refused.Headers.WwwAuthenticate.ToString().Should().Contain("max_age=300");
        (await ChangeAsync(api.Recent(tenant, subject), officeId, (Roles.Finance, null))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_site_manager_changes_roles_only_within_their_sites()
    {
        var tenant = await api.TenantAsync();
        var manager = await api.MemberAsync(tenant, (Roles.SiteManager, SiteA));
        var onA = await api.FieldUserAsync(tenant, (Roles.Operator, SiteA));
        var onB = await api.FieldUserAsync(tenant, (Roles.Operator, SiteB));

        (await ChangeAsync(manager, onA, (Roles.Mechanic, SiteA))).StatusCode.Should().Be(HttpStatusCode.OK);
        await TenantMembers.ExpectProblemAsync(await ChangeAsync(manager, onA, (Roles.Mechanic, SiteB)), HttpStatusCode.Forbidden, "identity.scope_exceeded");
        await TenantMembers.ExpectProblemAsync(await ChangeAsync(manager, onB, (Roles.Mechanic, SiteA)), HttpStatusCode.NotFound, "identity.user_not_found");
    }

    [Fact]
    public async Task A_field_user_cannot_get_an_office_role()
    {
        var (tenant, admin, _) = await AdminAsync(recent: true);
        var id = await api.FieldUserAsync(tenant, (Roles.Operator, null));

        await TenantMembers.ExpectProblemAsync(await ChangeAsync(admin, id, (Roles.Commercial, null)), HttpStatusCode.BadRequest, "identity.role_needs_account");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-02")]
    public async Task Keycloak_asks_for_a_second_factor_exactly_when_the_roles_demand_it()
    {
        var (tenant, admin, _) = await AdminAsync(recent: true);
        var email = $"r-{Guid.NewGuid():N}@example.test";
        var invited = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/identity/invitations", admin,
            new { firstName = "Role", lastName = "Changer", email, roles = new[] { new { role = Roles.Commercial, siteId = (Guid?)null } } }), HttpStatusCode.Created);
        var id = invited.GetProperty("userId").GetGuid();
        var account = await api.AccountIdAsync(email);
        (await api.RealmRolesAsync(account)).Should().NotContain("mfa-required");

        (await ChangeAsync(admin, id, (Roles.Controller, null))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.RealmRolesAsync(account)).Should().Contain("mfa-required", "controller is privileged");

        (await ChangeAsync(admin, id, (Roles.Commercial, null))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.RealmRolesAsync(account)).Should().NotContain("mfa-required");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-05")]
    public async Task Holders_of_a_custom_role_that_becomes_privileged_need_a_second_factor()
    {
        var (tenant, admin, _) = await AdminAsync(recent: true);
        var code = (await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/identity/roles", admin,
            new { name = "Viewers", permissions = new[] { IdentityPermissions.UsersRead } }), HttpStatusCode.Created)).GetProperty("code").GetString()!;
        var email = $"h-{Guid.NewGuid():N}@example.test";
        await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/identity/invitations", admin,
            new { firstName = "Role", lastName = "Holder", email, roles = new[] { new { role = code, siteId = (Guid?)null } } }), HttpStatusCode.Created);
        var account = await api.AccountIdAsync(email);
        (await api.RealmRolesAsync(account)).Should().NotContain("mfa-required");

        (await api.SendAsync(HttpMethod.Put, $"/api/v1/identity/roles/{code}", admin,
            new { name = "Viewers", permissions = new[] { IdentityPermissions.UsersRead, IdentityPermissions.UsersManage } })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await api.RealmRolesAsync(account)).Should().Contain("mfa-required");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-03")]
    public async Task A_resent_invitation_replaces_the_previous_link()
    {
        var (tenant, admin, _) = await AdminAsync(recent: true);
        var email = $"i-{Guid.NewGuid():N}@example.test";
        var invited = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/identity/invitations", admin,
            new { firstName = "Late", lastName = "Reader", email, roles = new[] { new { role = Roles.Commercial, siteId = (Guid?)null } } }), HttpStatusCode.Created);
        var id = invited.GetProperty("userId").GetGuid();
        var first = host.Sender.TokenFor(email);

        var resent = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{id}/invitation", admin), HttpStatusCode.OK);
        var second = host.Sender.TokenFor(email);

        resent.GetProperty("invitationId").GetGuid().Should().NotBe(invited.GetProperty("invitationId").GetGuid());
        second.Should().NotBe(first);
        await TenantMembers.ExpectProblemAsync(await AcceptAsync(first), HttpStatusCode.NotFound, "identity.invitation_invalid");
        (await AcceptAsync(second)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, $"/api/v1/identity/users/{id}/invitation", admin),
            HttpStatusCode.UnprocessableEntity, "identity.not_invited");
        (await api.LedgerAsync(tenant)).Should().Contain(e => e.Type == "identity.invitation_reissued.v1");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task Roles_changed_from_an_old_version_of_the_user_are_refused_and_the_newer_change_is_kept()
    {
        // Hardening plan H-C1: two administrators opened the same person; the second to save must not overwrite the first.
        var (tenant, admin, _) = await AdminAsync(recent: true);
        var id = await api.FieldUserAsync(tenant, (Roles.Operator, null));
        var opened = await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{id}", admin);
        var version = opened.Headers.ETag!.Tag;
        (await TenantMembers.JsonAsync(opened, HttpStatusCode.OK)).GetProperty("version").GetString().Should().Be(version);

        var first = await ChangeAsync(admin, id, version, (Roles.Mechanic, null));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag!.Tag.Should().NotBe(version);

        await TenantMembers.ExpectProblemAsync(await ChangeAsync(admin, id, version, (Roles.Storekeeper, null)), HttpStatusCode.Conflict, "request.version_conflict");
        var kept = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{id}", admin), HttpStatusCode.OK);
        kept.GetProperty("roles")[0].GetProperty("role").GetString().Should().Be(Roles.Mechanic);
        (await api.LedgerAsync(tenant)).Count(e => e.Type == "identity.user_roles_changed.v1").Should().Be(1, "the refused change left no trace");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task Of_many_role_changes_from_one_version_at_the_same_moment_exactly_one_is_applied()
    {
        var (tenant, admin, _) = await AdminAsync(recent: true);
        var id = await api.FieldUserAsync(tenant, (Roles.Operator, null));
        var version = (await api.SendAsync(HttpMethod.Get, $"/api/v1/identity/users/{id}", admin)).Headers.ETag!.Tag;
        string[] roles = [Roles.Mechanic, Roles.Storekeeper, Roles.Weighmaster, Roles.FuelAttendant];

        var changes = await Task.WhenAll(roles.Select(role => ChangeAsync(admin, id, version, (role, null))));

        changes.Count(c => c.StatusCode == HttpStatusCode.OK).Should().Be(1);
        changes.Count(c => c.StatusCode == HttpStatusCode.Conflict).Should().Be(roles.Length - 1, "never a 500");
    }

    private Task<HttpResponseMessage> ChangeAsync(string token, Guid user, string ifMatch, params (string Role, Guid? Site)[] roles) =>
        api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{user}/roles", token, new { roles = roles.Select(r => new { role = r.Role, siteId = r.Site }) },
            new Dictionary<string, string> { ["If-Match"] = ifMatch });

    private Task<HttpResponseMessage> AcceptAsync(string token) =>
        api.SendAsync(HttpMethod.Post, "/api/v1/identity/invitations/accept", null, new { token, password = "Correct-Horse-Battery-9" });

    private Task<HttpResponseMessage> ChangeAsync(string token, Guid user, params (string Role, Guid? Site)[] roles) =>
        api.SendAsync(HttpMethod.Put, $"/api/v1/identity/users/{user}/roles", token, new { roles = roles.Select(r => new { role = r.Role, siteId = r.Site }) });

    private async Task<(Guid Tenant, string Token, string Subject)> AdminAsync(bool recent)
    {
        var tenant = await api.TenantAsync();
        var subject = $"admin-{Guid.NewGuid():N}";
        await api.InsertAsync(tenant, "Office", [(Roles.TenantAdmin, null)], subject, employeeId: null);
        return (tenant, recent ? api.Recent(tenant, subject) : Karry.Testing.TestTokens.ForTenant(tenant, subject), subject);
    }
}
