using Karry.BuildingBlocks.Modules;
using Karry.Modules.Billing.Api;
using Karry.Modules.Flags.Api;
using Karry.Modules.Identity.Api;
using Karry.Modules.Materials.Api;
using Karry.Modules.Organization.Api;
using Karry.Modules.Tenancy.Api;
using Karry.Modules.Trust.Api;

namespace Karry.Host;

/// <summary>
/// The modules this host runs. Add a module here after generating it with <c>tools/new-module.sh</c>.
/// </summary>
internal static class ModuleCatalog
{
    public static IReadOnlyList<IModule> All { get; } =
    [
        new TenancyModule(),
        new IdentityModule(),
        new TrustModule(),
        new BillingModule(),
        new FlagsModule(),
        new OrganizationModule(),
        new MaterialsModule(),
    ];
}
