using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Organization.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Organization.Application;

/// <summary>A public holiday that falls on the same date every year.</summary>
public sealed record DemoHoliday(int Month, int Day, string Name);

/// <summary>A demo site with its shift pattern and its country's fixed public holidays.</summary>
public sealed record DemoSite(SiteInput Site, ShiftPatternInput Pattern, IReadOnlyList<DemoHoliday> Holidays);

/// <summary>
/// Sites with shifts and holidays in a demo tenant (US-P1-ORG-01/02), for local and demo environments only (the host
/// refuses <c>seed-demo</c> in production). Goes through the same operations as the API, as a Tenant Admin named
/// <c>seed-demo</c>, so every change is in the company's ledger and the shifts of the next 30 days are stored. The first
/// pattern starts on 1 January of the site's year; holidays are added for this year and the next, from tomorrow on.
/// Idempotent: a tenant that already has a site (archived ones included) is left as it is.
/// </summary>
public sealed class DemoSitesSeeder(
    SiteOperations sites, ShiftOperations shifts, ICallerAccess caller, AuthorizationCatalog catalog, ITenantContext context, TimeProvider clock)
{
    public const string Actor = "seed-demo";

    public async Task<int> SeedAsync(TenantId tenant, IReadOnlyList<DemoSite> demo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demo);
        context.Set(tenant);
        caller.Set(
            CallerAccess.From(catalog, [new AccessAssignment(Roles.TenantAdmin, catalog.DefaultPermissionsOf(Roles.TenantAdmin), new Dictionary<string, FieldAccess>())]),
            Actor, signedInAt: null);
        if ((await sites.ListAsync(includeArchived: true, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return 0;
        }

        foreach (var entry in demo)
        {
            var site = Check(await sites.CreateAsync(entry.Site, cancellationToken).ConfigureAwait(false), entry.Site.Code);
            var today = ShiftCalendar.Today(site.TimeZone, clock.GetUtcNow());
            Check(await shifts.SetPatternAsync(site.Id, new DateOnly(today.Year, 1, 1), entry.Pattern, cancellationToken).ConfigureAwait(false), entry.Site.Code);
            foreach (var year in new[] { today.Year, today.Year + 1 })
            {
                foreach (var holiday in entry.Holidays.Where(h => new DateOnly(year, h.Month, h.Day) > today))
                {
                    var date = new DateOnly(year, holiday.Month, holiday.Day);
                    Check(await shifts.SetNonWorkingDayAsync(site.Id, date, new NonWorkingDayInput("public-holiday", holiday.Name), cancellationToken)
                        .ConfigureAwait(false), entry.Site.Code);
                }
            }
        }

        return demo.Count;
    }

    private static T Check<T>(Result<T> result, string? site) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"Demo site {site}: {result.Error.Code}");
}
