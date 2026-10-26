using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Tenancy;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Materials.Application;

/// <summary>
/// Materials with their densities in a demo tenant (US-P1-AST-04), for local and demo environments only (the host
/// refuses <c>seed-demo</c> in production). Goes through the same operations as the API, as a Tenant Admin named
/// <c>seed-demo</c>, so every material is in the company's ledger. The densities are valid from 1 January of the
/// company's year (<see cref="MaterialInput.EffectiveFrom"/> is ignored). Idempotent: a tenant that already has a
/// material (archived ones included) is left as it is.
/// </summary>
public sealed class DemoMaterialsSeeder(MaterialOperations materials, ICallerAccess caller, AuthorizationCatalog catalog, ITenantContext context)
{
    public const string Actor = "seed-demo";

    public async Task<int> SeedAsync(TenantId tenant, IReadOnlyList<MaterialInput> demo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demo);
        context.Set(tenant);
        caller.Set(
            CallerAccess.From(catalog, [new AccessAssignment(Roles.TenantAdmin, catalog.DefaultPermissionsOf(Roles.TenantAdmin), new Dictionary<string, FieldAccess>())]),
            Actor, signedInAt: null);
        if ((await materials.ListAsync(includeArchived: true, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return 0;
        }

        var today = await materials.TodayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in demo)
        {
            var created = await materials.CreateAsync(entry with { EffectiveFrom = new DateOnly(today.Year, 1, 1) }, cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                throw new InvalidOperationException($"Demo material {entry.Code}: {created.Error.Code}");
            }
        }

        return demo.Count;
    }
}
