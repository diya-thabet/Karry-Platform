using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Tenancy;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.FieldOps.Application;

/// <summary>
/// Pre-start checklists in a demo tenant (P2.6), for local and demo environments only (the host refuses <c>seed-demo</c>
/// in production): an optional excavator checklist and a required haul-truck one, in French, English and Arabic. Goes
/// through the same operation as the API, as a Tenant Admin named <c>seed-demo</c>, so each version is in the company's
/// ledger. Idempotent: a tenant that already has a checklist is left as it is.
/// </summary>
public sealed class DemoChecklistsSeeder(ChecklistTemplates checklists, ICallerAccess caller, AuthorizationCatalog catalog, ITenantContext context)
{
    public const string Actor = "seed-demo";

    private static ChecklistItemInput Item(string code, bool critical, bool photo, string fr, string en, string ar) =>
        new(code, new Dictionary<string, string> { ["fr"] = fr, ["en"] = en, ["ar"] = ar }, critical, photo);

    /// <summary>The demo checklists by asset type, and whether they are required before a start.</summary>
    public static IReadOnlyDictionary<string, PublishChecklist> Checklists { get; } = new Dictionary<string, PublishChecklist>(StringComparer.Ordinal)
    {
        ["excavator"] = new(Required: false, [
            Item("brakes", true, true, "Freins et frein de stationnement", "Brakes and parking brake", "الفرامل وفرامل الوقوف"),
            Item("hydraulics", true, true, "Pas de fuite hydraulique (flexibles, vérins)", "No hydraulic leak (hoses, cylinders)", "لا تسرب هيدروليكي (الخراطيم، الأسطوانات)"),
            Item("extinguisher", true, false, "Extincteur présent et plombé", "Fire extinguisher present and sealed", "طفاية الحريق موجودة ومختومة"),
            Item("tracks", false, true, "Chenilles et galets", "Tracks and rollers", "الجنازير والبكرات"),
            Item("lights", false, false, "Feux, gyrophare et alarme de recul", "Lights, beacon and reverse alarm", "الأضواء والمنارة وإنذار الرجوع"),
            Item("mirrors", false, false, "Rétroviseurs et caméra", "Mirrors and camera", "المرايا والكاميرا"),
        ]),
        ["haul-truck"] = new(Required: true, [
            Item("brakes", true, true, "Freins et frein de stationnement", "Brakes and parking brake", "الفرامل وفرامل الوقوف"),
            Item("seatbelt", true, false, "Ceinture de sécurité", "Seat belt", "حزام الأمان"),
            Item("tyres", false, true, "Pneus (coupures, pression)", "Tyres (cuts, pressure)", "الإطارات (الجروح، الضغط)"),
            Item("lights", false, false, "Feux et alarme de recul", "Lights and reverse alarm", "الأضواء وإنذار الرجوع"),
        ]),
    };

    public async Task<int> SeedAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        context.Set(tenant);
        caller.Set(
            CallerAccess.From(catalog, [new AccessAssignment(Roles.TenantAdmin, catalog.DefaultPermissionsOf(Roles.TenantAdmin), new Dictionary<string, FieldAccess>())]),
            Actor, signedInAt: null);
        if ((await checklists.LatestAsync(cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return 0;
        }

        foreach (var (assetType, checklist) in Checklists)
        {
            var published = await checklists.PublishAsync(assetType, checklist, cancellationToken).ConfigureAwait(false);
            if (!published.IsSuccess)
            {
                throw new InvalidOperationException($"Demo checklist {assetType}: {published.Error.Code}");
            }
        }

        return Checklists.Count;
    }
}
