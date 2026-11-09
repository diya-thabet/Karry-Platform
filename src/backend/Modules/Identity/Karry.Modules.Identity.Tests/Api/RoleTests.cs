using System.Net;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>
/// Custom roles and field levels on the real pipeline (US-P1-IAM-05, US-P1-IAM-07 AC1): default roles are read-only
/// and undeletable, privileged changes need a recent sign-in (TC-P1-IAM-009), changes are in the ledger with before
/// and after, a role cannot gain rights its author lacks, and what a custom role holds is what its holders get, at
/// once on this instance.
/// </summary>
[Trait("Story", "US-P1-IAM-05")]
public sealed class RoleTests(IdentityHost host) : IClassFixture<IdentityHost>
{
    private const string Roles_ = "/api/v1/identity/roles";

    private static readonly string[] UnknownPermission = ["identity.users.everything"];

    private readonly TenantMembers api = new(host);

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-009")]
    public async Task A_default_role_cannot_be_deleted_or_have_its_permissions_changed()
    {
        var (_, admin) = await AdminAsync();

        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Delete, $"{Roles_}/{Roles.Operator}", admin), HttpStatusCode.UnprocessableEntity,
            "identity.default_role_undeletable");
        await TenantMembers.ExpectProblemAsync(
            await api.SendAsync(HttpMethod.Put, $"{Roles_}/{Roles.Operator}", admin, new { name = "Operator", permissions = new[] { IdentityPermissions.UsersRead } }),
            HttpStatusCode.UnprocessableEntity, "identity.default_role_readonly");
        var operatorRole = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, $"{Roles_}/{Roles.Operator}", admin), HttpStatusCode.OK);
        operatorRole.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        operatorRole.GetProperty("permissions").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-IAM-009")]
    public async Task A_privileged_change_without_a_recent_sign_in_asks_for_one_and_succeeds_after_it()
    {
        var (tenant, admin, subject) = await AdminWithSubjectAsync();
        var body = new { name = "User managers", permissions = new[] { IdentityPermissions.UsersRead, IdentityPermissions.UsersManage } };

        var refused = await api.SendAsync(HttpMethod.Post, Roles_, admin, body);
        await TenantMembers.ExpectProblemAsync(refused, HttpStatusCode.Unauthorized, PermissionMiddleware.StepUpCode);
        refused.Headers.WwwAuthenticate.ToString().Should().Contain("insufficient_user_authentication").And.Contain("max_age=300");

        var created = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, Roles_, api.Recent(tenant, subject), body), HttpStatusCode.Created);
        created.GetProperty("privileged").GetBoolean().Should().BeTrue("it holds a privileged permission");
        created.GetProperty("code").GetString().Should().StartWith(CustomRole.CodePrefix);
    }

    [Fact]
    public async Task A_non_privileged_role_is_created_without_step_up_and_a_clone_copies_its_basis()
    {
        var (_, admin, _) = await AdminWithSubjectAsync();

        var clone = await TenantMembers.JsonAsync(
            await api.SendAsync(HttpMethod.Post, Roles_, admin, new { name = "Night executive", basedOn = Roles.Executive }), HttpStatusCode.Created);
        var executive = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, $"{Roles_}/{Roles.Executive}", admin), HttpStatusCode.OK);

        clone.GetProperty("basedOn").GetString().Should().Be(Roles.Executive);
        clone.GetProperty("isDefault").GetBoolean().Should().BeFalse();
        Strings(clone, "permissions").Should().Equal(Strings(executive, "permissions"));
        clone.GetProperty("fieldLevels").GetRawText().Should().Be(executive.GetProperty("fieldLevels").GetRawText());
    }

    [Fact]
    public async Task Unknown_permissions_fields_and_levels_are_rejected()
    {
        var (_, admin) = await AdminAsync();

        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, Roles_, admin, new { name = "X", permissions = UnknownPermission }),
            HttpStatusCode.BadRequest, "identity.permission_unknown");
        await TenantMembers.ExpectProblemAsync(
            await api.SendAsync(HttpMethod.Put, $"{Roles_}/{Roles.Operator}/field-levels", admin, new { fieldLevels = new Dictionary<string, string> { ["probe.quote.margin"] = "masked" } }),
            HttpStatusCode.BadRequest, "identity.field_unknown");
        await TenantMembers.ExpectProblemAsync(
            await api.SendAsync(HttpMethod.Put, $"{Roles_}/{Roles.Operator}/field-levels", admin, new { fieldLevels = new Dictionary<string, string> { [ProbeModule.UnitPrice] = "2" } }),
            HttpStatusCode.BadRequest, "identity.field_level_invalid");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, Roles_, admin, new { name = "Y", basedOn = "custom-nope" }),
            HttpStatusCode.BadRequest, "identity.role_unknown");
    }

    [Fact]
    public async Task Two_custom_roles_cannot_share_a_name()
    {
        var (_, admin) = await AdminAsync();

        (await api.SendAsync(HttpMethod.Post, Roles_, admin, new { name = "Dispatch" })).StatusCode.Should().Be(HttpStatusCode.Created);
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, Roles_, admin, new { name = "Dispatch" }), HttpStatusCode.Conflict, "identity.role_exists");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-06")]
    public async Task A_role_manager_cannot_make_a_role_with_privileged_rights_they_lack()
    {
        var (tenant, admin, subject) = await AdminWithSubjectAsync();
        var managers = await CreateAsync(api.Recent(tenant, subject),
            new { name = "Role managers", permissions = new[] { IdentityPermissions.RolesRead, IdentityPermissions.RolesManage } });
        var manager = await api.MemberAsync(tenant, host.Clock.GetUtcNow(), (managers, null));

        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, Roles_, manager, new { name = "Back door", permissions = new[] { IdentityPermissions.UsersManage } }),
            HttpStatusCode.Forbidden, "identity.escalation");
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, Roles_, manager, new { name = "Admin copy", basedOn = Roles.TenantAdmin }),
            HttpStatusCode.Forbidden, "identity.escalation");
        // Rights they hold themselves are fine (the refusals above were about the rights, not the caller).
        (await api.SendAsync(HttpMethod.Post, Roles_, manager, new { name = "Role viewers", permissions = new[] { IdentityPermissions.RolesRead } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task What_a_custom_role_holds_is_what_its_holders_get_at_once_after_a_change()
    {
        var (tenant, admin) = await AdminAsync();
        var viewers = await CreateAsync(admin, new { name = "User viewers", permissions = new[] { IdentityPermissions.UsersRead } });
        var viewer = await api.MemberAsync(tenant, (viewers, null));

        (await api.SendAsync(HttpMethod.Get, "/api/v1/identity/users", viewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Get, Roles_, viewer));

        (await api.SendAsync(HttpMethod.Put, $"{Roles_}/{viewers}", admin, new { name = "User viewers", permissions = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // No 30 s wait on the instance that made the change.
        await TenantMembers.ExpectForbiddenAsync(await api.SendAsync(HttpMethod.Get, "/api/v1/identity/users", viewer));
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-07")]
    public async Task Field_levels_set_on_a_role_decide_what_its_holders_see_and_showing_a_field_needs_step_up()
    {
        var (tenant, admin, subject) = await AdminWithSubjectAsync();
        var commercial = await api.MemberAsync(tenant, (Roles.Commercial, null));
        (await QuoteAsync(commercial)).GetProperty("unitPrice").GetDecimal().Should().Be(7_431_517.25m, "commercial reads prices by default");

        var masked = new { fieldLevels = new Dictionary<string, string> { [ProbeModule.UnitPrice] = "masked" } };
        var levels = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Put, $"{Roles_}/{Roles.Commercial}/field-levels", admin, masked), HttpStatusCode.OK);
        levels.GetProperty("fieldLevels").GetProperty(ProbeModule.UnitPrice).GetString().Should().Be("masked");

        var quote = await QuoteAsync(commercial);
        quote.TryGetProperty("unitPrice", out _).Should().BeFalse();
        Strings(quote, "maskedFields").Should().Equal("unitPrice");

        // Showing it again is a privileged change.
        var shown = new { fieldLevels = new Dictionary<string, string>() };
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Put, $"{Roles_}/{Roles.Commercial}/field-levels", admin, shown),
            HttpStatusCode.Unauthorized, PermissionMiddleware.StepUpCode);
        (await api.SendAsync(HttpMethod.Put, $"{Roles_}/{Roles.Commercial}/field-levels", api.Recent(tenant, subject), shown)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await QuoteAsync(commercial)).GetProperty("unitPrice").GetDecimal().Should().Be(7_431_517.25m);
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-07")]
    public async Task A_custom_role_masks_fields_it_does_not_mention()
    {
        var (tenant, admin) = await AdminAsync();
        var sellers = await CreateAsync(admin, new { name = "Junior sellers", permissions = new[] { ProbeModule.QuotesRead } });
        var seller = await api.MemberAsync(tenant, (sellers, null));

        var quote = await QuoteAsync(seller);

        quote.TryGetProperty("unitPrice", out _).Should().BeFalse();
        Strings(quote, "maskedFields").Should().Equal("unitPrice");
    }

    [Fact]
    public async Task A_custom_role_someone_holds_cannot_be_deleted_and_one_nobody_holds_can()
    {
        var (tenant, admin) = await AdminAsync();
        var held = await CreateAsync(admin, new { name = "Loaders", office = false });
        var free = await CreateAsync(admin, new { name = "Spare" });
        await api.FieldUserAsync(tenant, (held, null));

        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Delete, $"{Roles_}/{held}", admin), HttpStatusCode.Conflict, "identity.role_in_use");
        (await api.SendAsync(HttpMethod.Delete, $"{Roles_}/{free}", admin)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Get, $"{Roles_}/{free}", admin), HttpStatusCode.NotFound, "identity.role_not_found");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-03")]
    public async Task A_custom_field_role_is_given_like_a_default_one_and_an_office_one_needs_an_account()
    {
        var (_, admin) = await AdminAsync();
        var loaders = await CreateAsync(admin, new { name = "Loaders", office = false });
        var clerks = await CreateAsync(admin, new { name = "Clerks", office = true });

        (await api.SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", admin, FieldUser(loaders))).StatusCode.Should().Be(HttpStatusCode.Created);
        await TenantMembers.ExpectProblemAsync(await api.SendAsync(HttpMethod.Post, "/api/v1/identity/field-users", admin, FieldUser(clerks)),
            HttpStatusCode.BadRequest, "identity.role_needs_account");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-05")]
    public async Task Every_change_is_in_the_ledger_with_before_and_after()
    {
        var (tenant, admin) = await AdminAsync();
        var code = await CreateAsync(admin, new { name = "Auditors", permissions = new[] { IdentityPermissions.RolesRead } });
        (await api.SendAsync(HttpMethod.Put, $"{Roles_}/{code}", admin, new { name = "Auditors", permissions = new[] { IdentityPermissions.RolesRead, IdentityPermissions.UsersRead } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Put, $"{Roles_}/{Roles.Operator}/field-levels", admin,
            new { fieldLevels = new Dictionary<string, string> { [ProbeModule.UnitPrice] = "hidden" } })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Delete, $"{Roles_}/{code}", admin)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var ledger = (await api.LedgerAsync(tenant)).Where(e => e.Type.Contains("role", StringComparison.Ordinal) || e.Type.Contains("field_levels", StringComparison.Ordinal)).ToList();

        ledger.Select(e => e.Type).Should().Equal(
            "identity.role_created.v1", "identity.role_changed.v1", "identity.field_levels_changed.v1", "identity.role_deleted.v1");
        var changed = ledger[1].Payload;
        Strings(changed.GetProperty("before"), "permissions").Should().Equal(IdentityPermissions.RolesRead);
        Strings(changed.GetProperty("after"), "permissions").Should().Equal(IdentityPermissions.RolesRead, IdentityPermissions.UsersRead);
        var fields = ledger[2].Payload;
        fields.GetProperty("before").GetProperty(ProbeModule.UnitPrice).GetString().Should().Be("masked");
        fields.GetProperty("after").GetProperty(ProbeModule.UnitPrice).GetString().Should().Be("hidden");
    }

    [Fact]
    public async Task The_catalogue_lists_the_declared_permissions_and_fields()
    {
        var (_, admin) = await AdminAsync();

        var catalog = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, "/api/v1/identity/permissions", admin), HttpStatusCode.OK);
        var roles = await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, Roles_, admin), HttpStatusCode.OK);

        catalog.GetProperty("permissions").EnumerateArray().Should().Contain(p => p.GetProperty("code").GetString() == IdentityPermissions.UsersManage
            && p.GetProperty("privileged").GetBoolean() && p.GetProperty("scope").GetString() == "site");
        catalog.GetProperty("sensitiveFields").EnumerateArray().Select(f => f.GetProperty("code").GetString()).Should().Contain(ProbeModule.UnitPrice);
        roles.GetProperty("items").EnumerateArray().Count(r => r.GetProperty("isDefault").GetBoolean()).Should().Be(Roles.Defaults.Count);
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task A_custom_role_changed_from_an_old_version_is_refused_and_not_deleted()
    {
        var (_, admin) = await AdminAsync();
        var code = await CreateAsync(admin, new { name = "Night commercial", basedOn = Roles.Commercial });
        var opened = await api.SendAsync(HttpMethod.Get, $"{Roles_}/{code}", admin);
        var version = opened.Headers.ETag!.Tag;
        var role = await TenantMembers.JsonAsync(opened, HttpStatusCode.OK);
        role.GetProperty("version").GetString().Should().Be(version);
        var permissions = Strings(role, "permissions");

        var first = await Change(admin, code, new { name = "Night sales", permissions }, version);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        await TenantMembers.ExpectProblemAsync(await Change(admin, code, new { name = "Late sales", permissions }, version), HttpStatusCode.Conflict, "request.version_conflict");
        await TenantMembers.ExpectProblemAsync(
            await api.SendAsync(HttpMethod.Delete, $"{Roles_}/{code}", admin, headers: new Dictionary<string, string> { ["If-Match"] = version }),
            HttpStatusCode.Conflict, "request.version_conflict");

        (await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, $"{Roles_}/{code}", admin), HttpStatusCode.OK))
            .GetProperty("name").GetString().Should().Be("Night sales", "the newer change is kept and the role still exists");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task A_default_role_s_field_levels_have_a_version_from_their_first_change()
    {
        // A default role the company never changed has version 0; its first change creates the settings, which then carry
        // the version. Many first changes at once: exactly one applies (the others meet its row), never a 500.
        var (_, admin) = await AdminAsync();
        var opened = await api.SendAsync(HttpMethod.Get, $"{Roles_}/{Roles.Commercial}", admin);
        opened.Headers.ETag!.Tag.Should().Be("\"0\"");
        var masked = new { fieldLevels = new Dictionary<string, string> { [ProbeModule.UnitPrice] = "masked" } };

        var changes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => FieldLevels(admin, Roles.Commercial, masked, "\"0\"")));

        changes.Count(c => c.StatusCode == HttpStatusCode.OK).Should().Be(1);
        changes.Count(c => c.StatusCode == HttpStatusCode.Conflict).Should().Be(5);
        var current = (await api.SendAsync(HttpMethod.Get, $"{Roles_}/{Roles.Commercial}", admin)).Headers.ETag!.Tag;
        current.Should().NotBe("\"0\"");
        (await FieldLevels(admin, Roles.Commercial, masked, current)).StatusCode.Should().Be(HttpStatusCode.OK, "from the current version");
    }

    private Task<HttpResponseMessage> Change(string token, string code, object body, string ifMatch) =>
        api.SendAsync(HttpMethod.Put, $"{Roles_}/{code}", token, body, new Dictionary<string, string> { ["If-Match"] = ifMatch });

    private Task<HttpResponseMessage> FieldLevels(string token, string code, object body, string ifMatch) =>
        api.SendAsync(HttpMethod.Put, $"{Roles_}/{code}/field-levels", token, body, new Dictionary<string, string> { ["If-Match"] = ifMatch });

    private async Task<(Guid Tenant, string Token)> AdminAsync()
    {
        var tenant = await api.TenantAsync();
        return (tenant, await api.MemberAsync(tenant, host.Clock.GetUtcNow(), (Roles.TenantAdmin, null)));
    }

    /// <summary>An administrator signed in long ago (privileged changes ask for step-up) and their subject.</summary>
    private async Task<(Guid Tenant, string Token, string Subject)> AdminWithSubjectAsync()
    {
        var tenant = await api.TenantAsync();
        var subject = $"admin-{Guid.NewGuid():N}";
        await api.InsertAsync(tenant, "Office", [(Roles.TenantAdmin, null)], subject, employeeId: null);
        return (tenant, Karry.Testing.TestTokens.ForTenant(tenant, subject), subject);
    }

    private async Task<string> CreateAsync(string token, object body) =>
        (await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Post, Roles_, token, body), HttpStatusCode.Created)).GetProperty("code").GetString()!;

    private async Task<JsonElement> QuoteAsync(string token) =>
        await TenantMembers.JsonAsync(await api.SendAsync(HttpMethod.Get, "/api/v1/probe/quote", token), HttpStatusCode.OK);

    private static List<string> Strings(JsonElement element, string property) =>
        [.. element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];

    private static object FieldUser(string role) => new
    {
        firstName = "Field",
        lastName = "Worker",
        employeeId = $"E-{Guid.NewGuid():N}"[..20],
        pin = "730194",
        roles = new[] { new { role, siteId = (Guid?)null } },
    };
}
