using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Modules;
using Karry.Modules.Assets.Api;
using Karry.Modules.Billing.Api;
using Karry.Modules.Flags.Api;
using Karry.Modules.Identity.Api;
using Karry.Modules.Imports.Api;
using Karry.Modules.Materials.Api;
using Karry.Modules.Organization.Api;
using Karry.Modules.Tenancy.Api;
using Karry.Modules.Trust.Api;

namespace Karry.Workers;

/// <summary>
/// The modules whose work this host runs, for their declarations only: the permission catalogue, so the import runner
/// rebuilds a requester's access exactly as the API does (ADR-0027 §10). Keep it in step with the API's module list
/// (<c>Karry.Host/ModuleCatalog.cs</c>); a missing module's permissions would grant nothing (fail closed).
/// </summary>
internal static class WorkerModules
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
        new AssetsModule(),
        new ImportsModule(),
    ];

    public static AuthorizationCatalog Catalog { get; } = AuthorizationCatalog.From(All);
}
