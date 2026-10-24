using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.SharedKernel.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>Effective access from role assignments per scope (US-P1-IAM-06 AC1, ADR-0022).</summary>
[Trait("Story", "US-P1-IAM-06")]
public sealed class CallerAccessTests
{
    private static readonly Guid SiteA = Guid.Parse("01970000-0000-7000-8000-0000000000a1");
    private static readonly Guid SiteB = Guid.Parse("01970000-0000-7000-8000-0000000000b1");
    private static readonly Guid WarehouseA1 = Guid.Parse("01970000-0000-7000-8000-0000000000a2");

    internal static readonly AuthorizationCatalog Catalog = AuthorizationCatalog.From([new DeclaringModule()]);

    [Fact]
    public void A_tenant_wide_role_grants_every_kind_of_permission_everywhere()
    {
        var access = CallerAccess.From(Catalog, [Assignment("probe.billing.manage", "probe.ticket.read", "probe.stock.move")]);

        access.ScopeOf("probe.billing.manage").Should().BeSameAs(ScopeSet.Everywhere);
        access.ScopeOf("probe.ticket.read").TenantWide.Should().BeTrue();
        access.ScopeOf("probe.stock.move").CoversWarehouse(WarehouseA1, SiteB).Should().BeTrue();
    }

    [Fact]
    public void A_site_role_grants_site_and_warehouse_permissions_on_that_site_only_and_no_company_permission()
    {
        var access = CallerAccess.From(Catalog, [Assignment("probe.billing.manage", "probe.ticket.read", "probe.stock.move") with { SiteId = SiteA }]);

        access.Has("probe.billing.manage").Should().BeFalse("a role held on one site does not manage the company");
        var tickets = access.ScopeOf("probe.ticket.read");
        tickets.TenantWide.Should().BeFalse();
        tickets.CoversSite(SiteA).Should().BeTrue();
        tickets.CoversSite(SiteB).Should().BeFalse();
        access.ScopeOf("probe.stock.move").CoversWarehouse(WarehouseA1, SiteA).Should().BeTrue("a warehouse of the site");
        access.ScopeOf("probe.stock.move").CoversWarehouse(WarehouseA1, SiteB).Should().BeFalse();
    }

    [Fact]
    public void A_warehouse_role_grants_only_warehouse_permissions_on_that_warehouse()
    {
        var access = CallerAccess.From(Catalog, [Assignment("probe.ticket.read", "probe.stock.move") with { WarehouseId = WarehouseA1 }]);

        access.Has("probe.ticket.read").Should().BeFalse("a storekeeper of one warehouse sees no site data through it");
        var stock = access.ScopeOf("probe.stock.move");
        stock.CoversWarehouse(WarehouseA1, SiteB).Should().BeTrue();
        stock.CoversWarehouse(Guid.CreateVersion7(), SiteB).Should().BeFalse();
        stock.CoversSite(SiteB).Should().BeFalse();
    }

    [Fact]
    public void Scopes_of_several_assignments_add_up()
    {
        var access = CallerAccess.From(Catalog,
        [
            Assignment("probe.ticket.read") with { SiteId = SiteA },
            Assignment("probe.ticket.read") with { SiteId = SiteB },
        ]);

        access.ScopeOf("probe.ticket.read").Sites.Should().BeEquivalentTo([SiteA, SiteB]);
    }

    [Fact]
    public void A_permission_no_module_declares_grants_nothing()
    {
        // A custom role saved before a permission was withdrawn from the catalogue.
        var access = CallerAccess.From(Catalog, [Assignment("probe.ticket.delete-all")]);

        access.Has("probe.ticket.delete-all").Should().BeFalse();
        access.Granted.Should().BeEmpty();
    }

    [Fact]
    public void No_assignment_is_nobody()
    {
        CallerAccess.From(Catalog, []).Should().BeSameAs(CallerAccess.Nobody);
        CallerAccess.Nobody.Has("probe.ticket.read").Should().BeFalse();
        CallerAccess.Nobody.FieldAccessFor("probe.ticket.unit-price", SiteA).Should().Be(FieldAccess.Hidden);
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-07")]
    public void A_sensitive_field_follows_the_roles_held_on_the_object_site_and_the_highest_level_wins()
    {
        var access = CallerAccess.From(Catalog,
        [
            Fields(FieldAccess.Masked) with { SiteId = SiteA },
            Fields(FieldAccess.Read) with { SiteId = SiteB },
        ]);

        access.FieldAccessFor("probe.ticket.unit-price", SiteA).Should().Be(FieldAccess.Masked, "weighmaster on A, site manager on B");
        access.FieldAccessFor("probe.ticket.unit-price", SiteB).Should().Be(FieldAccess.Read);
        access.FieldAccessFor("probe.ticket.unit-price", Guid.CreateVersion7()).Should().Be(FieldAccess.Hidden, "no role on that site");
        access.FieldAccessFor("probe.ticket.unit-price", null).Should().Be(FieldAccess.Hidden, "an object of no site: only company-wide roles count");
        access.FieldAccessFor("probe.ticket.margin", SiteB).Should().Be(FieldAccess.Hidden, "a field the role does not list");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-07")]
    public void A_tenant_wide_role_applies_to_the_fields_of_every_site()
    {
        var access = CallerAccess.From(Catalog, [Fields(FieldAccess.Write), Fields(FieldAccess.Masked) with { SiteId = SiteA }]);

        access.FieldAccessFor("probe.ticket.unit-price", SiteA).Should().Be(FieldAccess.Write);
        access.FieldAccessFor("probe.ticket.unit-price", null).Should().Be(FieldAccess.Write);
    }

    private static AccessAssignment Assignment(params string[] permissions) =>
        new("custom", permissions.ToHashSet(StringComparer.Ordinal), new Dictionary<string, FieldAccess>());

    private static AccessAssignment Fields(FieldAccess price) =>
        new("custom", new HashSet<string>(), new Dictionary<string, FieldAccess> { ["probe.ticket.unit-price"] = price });

    internal sealed class DeclaringModule : IModule
    {
        public string Name => "probe";

        public IReadOnlyList<PermissionDefinition> Permissions =>
        [
            new("probe.billing.manage", ScopeKind.Tenant, privileged: true, DefaultRoles.TenantAdmin),
            new("probe.ticket.read", ScopeKind.Site, privileged: false, DefaultRoles.TenantAdmin, DefaultRoles.Weighmaster),
            new("probe.stock.move", ScopeKind.Warehouse, privileged: false, DefaultRoles.Storekeeper),
        ];

        public IReadOnlyList<SensitiveFieldDefinition> SensitiveFields =>
        [
            new("probe.ticket.unit-price", new Dictionary<string, FieldAccess> { [DefaultRoles.Weighmaster] = FieldAccess.Masked }),
        ];

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }
}

/// <summary>Declarations are checked when the host starts (ADR-0022).</summary>
[Trait("Story", "US-P1-IAM-04")]
public sealed class AuthorizationCatalogTests
{
    [Fact]
    public void The_matrix_row_of_a_default_role_is_the_permissions_that_name_it()
    {
        CallerAccessTests.Catalog.DefaultPermissionsOf(DefaultRoles.TenantAdmin).Should().BeEquivalentTo(["probe.billing.manage", "probe.ticket.read"]);
        CallerAccessTests.Catalog.DefaultPermissionsOf(DefaultRoles.Operator).Should().BeEmpty();
        CallerAccessTests.Catalog.SensitiveField("probe.ticket.unit-price")!.DefaultFor(DefaultRoles.Weighmaster).Should().Be(FieldAccess.Masked);
        CallerAccessTests.Catalog.SensitiveField("probe.ticket.unit-price")!.DefaultFor("custom-role").Should().Be(FieldAccess.Masked, "the fallback");
    }

    public static TheoryData<string, string> Wrong => new()
    {
        { "module", "probe.ticket.read|other.ticket.read" },
        { "role", "probe.ticket.read@nobody" },
        { "duplicate", "probe.ticket.read|probe.ticket.read" },
    };

    [Theory]
    [MemberData(nameof(Wrong))]
    public void A_wrong_declaration_stops_the_host(string problem, string declarations)
    {
        var module = new ListModule(declarations.Split('|').Select(d => d.Split('@')).Select(d =>
            new PermissionDefinition(d[0], ScopeKind.Site, privileged: false, d.Length > 1 ? d[1] : DefaultRoles.Operator)).ToList());

        var build = () => AuthorizationCatalog.From([module]);

        build.Should().Throw<InvalidOperationException>(problem).WithMessage("Invalid authorization declarations*");
    }

    [Theory]
    [InlineData("probe.ticket")]
    [InlineData("Probe.ticket.read")]
    [InlineData("probe.ticket.read.extra")]
    [InlineData("probe..read")]
    public void A_permission_code_is_module_resource_action(string code)
    {
        var create = () => new PermissionDefinition(code, ScopeKind.Site, privileged: false);

        create.Should().Throw<ArgumentException>();
    }

    private sealed class ListModule(IReadOnlyList<PermissionDefinition> permissions) : IModule
    {
        public string Name => "probe";

        public IReadOnlyList<PermissionDefinition> Permissions => permissions;

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }
}
