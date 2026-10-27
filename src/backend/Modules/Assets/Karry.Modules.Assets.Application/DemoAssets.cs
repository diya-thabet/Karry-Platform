using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Domain;
using Karry.Modules.Organization.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Tenancy;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Assets.Application;

/// <summary>
/// A demo machine on the site with code <c>Site</c>. Its meters start at <c>Asset.Meters</c>' initial readings on their
/// installation date; <c>Worked</c> (by meter kind: <c>hours</c>, <c>km</c>) is added by a reading an hour ago, so each
/// meter history has a reading after the initial one.
/// </summary>
public sealed record DemoAsset(string Site, AssetInput Asset, IReadOnlyDictionary<string, decimal> Worked);

/// <summary>
/// Machines with meters in a demo tenant (P1.13), for local and demo environments only (the host refuses
/// <c>seed-demo</c> in production). Goes through the same operations as the API, as a Tenant Admin named
/// <c>seed-demo</c>, so every machine and reading is in the company's ledger. Runs after the demo sites. Idempotent: a
/// tenant that already has a machine (disposed ones included) is left as it is.
/// </summary>
public sealed class DemoAssetsSeeder(
    AssetOperations assets, MeterOperations meters, ISiteDirectory sites, ICallerAccess caller, AuthorizationCatalog catalog, ITenantContext context,
    TimeProvider clock)
{
    public const string Actor = "seed-demo";

    public async Task<int> SeedAsync(TenantId tenant, IReadOnlyList<DemoAsset> demo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demo);
        context.Set(tenant);
        caller.Set(
            CallerAccess.From(catalog, [new AccessAssignment(Roles.TenantAdmin, catalog.DefaultPermissionsOf(Roles.TenantAdmin), new Dictionary<string, FieldAccess>())]),
            Actor, signedInAt: null);
        foreach (var status in new[] { (string?)null, "disposed" })
        {
            var found = await assets.SearchAsync(new AssetSearch(null, null, null, status, null, 1), cancellationToken).ConfigureAwait(false);
            if (found.Value.Items.Count > 0)
            {
                return 0;
            }
        }

        var siteIds = (await sites.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(s => s.Code, s => s.Id, StringComparer.Ordinal);
        var readAt = clock.GetUtcNow().AddHours(-1);
        foreach (var entry in demo)
        {
            if (!siteIds.TryGetValue(entry.Site, out var siteId))
            {
                throw new InvalidOperationException($"Demo machine {entry.Asset.Code}: no site {entry.Site}");
            }

            var registered = await assets.RegisterAsync(entry.Asset with { SiteId = siteId }, cancellationToken).ConfigureAwait(false);
            if (!registered.IsSuccess)
            {
                throw new InvalidOperationException($"Demo machine {entry.Asset.Code}: {registered.Error.Code}");
            }

            var id = registered.Value.Id;
            foreach (var meter in (await meters.ListAsync(id, cancellationToken).ConfigureAwait(false)).Value)
            {
                var worked = entry.Worked.GetValueOrDefault(AssetRules.MeterKindCode(meter.Kind));
                var read = await meters.RecordAsync(id, meter.Id, new ReadingInput(meter.LastValue + worked, readAt), cancellationToken).ConfigureAwait(false);
                if (!read.IsSuccess)
                {
                    throw new InvalidOperationException($"Demo machine {entry.Asset.Code} reading: {read.Error.Code}");
                }
            }
        }

        return demo.Count;
    }
}
