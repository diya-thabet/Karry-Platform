using Karry.BuildingBlocks.Authorization;
using Karry.Modules.Identity.Api;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Identity.Tests.Api;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Identity.Tests.Domain;

/// <summary>Shape rules of custom roles and how the tenant's roles resolve (US-P1-IAM-05, US-P1-IAM-07 AC1).</summary>
[Trait("Story", "US-P1-IAM-05")]
public sealed class CustomRoleTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly AuthorizationCatalog Catalog = AuthorizationCatalog.From([new IdentityModule(), new ProbeModule()]);
    private static readonly Dictionary<string, FieldAccess> NoLevels = [];

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("A name that is far too long for a role because roles are short labels in a list")]
    [InlineData("Night\u0007shift")]
    public void A_role_needs_a_short_printable_name(string? name) =>
        CustomRole.Create(clock, Tenant, name, null, null, office: true, [], NoLevels, "admin").Error!.Code.Should().Be("identity.role_name_invalid");

    [Fact]
    public void A_new_role_gets_a_code_from_its_id_and_records_its_content()
    {
        var role = CustomRole.Create(clock, Tenant, "  Night   dispatch ", null, Roles.SiteManager, office: true,
            [IdentityPermissions.UsersRead, IdentityPermissions.UsersRead], NoLevels, "admin").Value;

        role.Name.Should().Be("Night dispatch");
        role.Code.Should().Be(CustomRole.CodePrefix + role.Id.Value.ToString("N"));
        CustomRole.IsCustomCode(role.Code).Should().BeTrue();
        CustomRole.IsCustomCode(Roles.SiteManager).Should().BeFalse();
        role.Permissions.Should().Equal(IdentityPermissions.UsersRead);
        role.DomainEvents.Single().Should().BeOfType<RoleCreated>().Which.BasedOn.Should().Be(Roles.SiteManager);
    }

    [Fact]
    public void A_rename_records_no_event_and_an_unchanged_role_reports_no_change()
    {
        var role = CustomRole.Create(clock, Tenant, "Dispatch", null, null, office: true, [IdentityPermissions.UsersRead], NoLevels, "admin").Value;
        role.ClearDomainEvents();

        role.Change(clock, "Dispatch", null, [IdentityPermissions.UsersRead], NoLevels, "admin").Value.Should().BeFalse();
        role.Change(clock, "Dispatchers", null, [IdentityPermissions.UsersRead], NoLevels, "admin").Value.Should().BeTrue();
        role.DomainEvents.Should().BeEmpty("the name is not what the role gives");

        role.Change(clock, "Dispatchers", null, [], NoLevels, "admin").Value.Should().BeTrue();
        role.DomainEvents.Single().Should().BeOfType<RoleChanged>().Which.After.Permissions.Should().BeEmpty();
    }

    [Fact]
    public void A_custom_role_is_privileged_when_it_holds_a_privileged_permission_or_shows_a_sensitive_field()
    {
        var plain = Custom("Plain", [IdentityPermissions.UsersRead], NoLevels);
        var managing = Custom("Managing", [IdentityPermissions.UsersManage], NoLevels);
        var seeing = Custom("Seeing", [], new() { [ProbeModule.UnitPrice] = FieldAccess.Read });

        var roles = TenantRoles.Build(Catalog, [plain, managing, seeing], []);

        roles.Find(plain.Code)!.Privileged.Should().BeFalse();
        roles.Find(managing.Code)!.Privileged.Should().BeTrue();
        roles.Find(seeing.Code)!.Privileged.Should().BeTrue();
        roles.Find(plain.Code)!.FieldLevels[ProbeModule.UnitPrice].Should().Be(FieldAccess.Masked, "a field it does not mention gets the fallback");
    }

    [Fact]
    public void A_role_the_tenant_no_longer_has_gives_nothing_and_counts_as_privileged_for_mfa()
    {
        var roles = TenantRoles.Build(Catalog, [], []);
        var gone = new RoleAssignment("custom-0197", null);

        var access = roles.Resolve(gone);

        access.Permissions.Should().BeEmpty();
        CallerAccess.From(Catalog, [access]).FieldAccessFor(ProbeModule.UnitPrice, null).Should().Be(FieldAccess.Hidden);
        UserAccount.CreateFieldUser(clock, Tenant, "A", "B", "E-1", "730194", [new(Roles.Operator, null)], "admin").Value
            .RequiresMfa(roles.Lookup).Should().BeFalse();
        roles.Lookup(gone.Role).Should().BeNull();
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-07")]
    public void A_default_role_keeps_its_catalogue_permissions_and_takes_the_tenants_field_levels()
    {
        var settings = DefaultRoleSettings.For(clock, Tenant, Roles.Commercial);
        settings.Change(clock, new Dictionary<string, FieldAccess> { [ProbeModule.UnitPrice] = FieldAccess.Hidden },
            new Dictionary<string, FieldAccess> { [ProbeModule.UnitPrice] = FieldAccess.Read },
            new Dictionary<string, FieldAccess> { [ProbeModule.UnitPrice] = FieldAccess.Hidden }, "admin").Should().BeTrue();

        var commercial = TenantRoles.Build(Catalog, [], [settings]).Find(Roles.Commercial)!;

        commercial.Permissions.Should().BeEquivalentTo(Catalog.DefaultPermissionsOf(Roles.Commercial));
        commercial.FieldLevels[ProbeModule.UnitPrice].Should().Be(FieldAccess.Hidden);
        FluentActions.Invoking(() => DefaultRoleSettings.For(clock, Tenant, "custom-x")).Should().Throw<ArgumentException>();
    }

    private CustomRole Custom(string name, string[] permissions, Dictionary<string, FieldAccess> levels) =>
        CustomRole.Create(clock, Tenant, name, null, null, office: true, permissions, levels, "admin").Value;
}
