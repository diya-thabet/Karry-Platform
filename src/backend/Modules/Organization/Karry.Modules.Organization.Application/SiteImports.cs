using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Organization.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using SupportedCurrency = Karry.SharedKernel.Monetary.Currency;

namespace Karry.Modules.Organization.Application;

/// <summary>
/// The <c>sites</c> import (US-P1-ORG-03, ADR-0027): one row per site, keyed by its code. A new code creates the site
/// (within the site limit, counted for the whole file); a known code changes its details, keeping its boundary, which
/// the template does not carry; an identical row is left as it is. An archived site is an error: it is restored on
/// screen, where the limit is checked. Validation prepares the changes on the loaded sites and never saves them; the
/// commit prepares them again, updates the shifts of changed sites and saves the whole file in one transaction.
/// </summary>
public sealed class SiteImport(ISiteStore store, ShiftInstances shifts, ITenantDirectory tenants, ILimitGuard limits, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
    : IImportTarget
{
    public static readonly Error Archived = Error.Conflict("organization.site_archived", "This site is archived; restore it before importing it.");

    private static readonly Dictionary<string, string> ColumnOfError = new(StringComparer.Ordinal)
    {
        ["organization.site_code_invalid"] = "code",
        ["organization.site_name_invalid"] = "name",
        ["organization.site_type_invalid"] = "type",
        ["organization.site_address_invalid"] = "address",
        ["organization.site_location_invalid"] = "latitude",
        ["organization.time_zone_unknown"] = "timeZone",
        ["organization.currency_unsupported"] = "currency",
    };

    public string Kind => "sites";

    public string Permission => OrganizationPermissions.SitesManage;

    public IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new("code", "Code", "Code", Required: true, Example: "CAR-NORD"),
        new("name", "Name", "Nom", Required: true, Example: "Carrière Nord"),
        new("type", "Type", "Type", Required: true, List: "types", Example: "quarry"),
        new("address", "Address", "Adresse", Required: true, Example: "Route de Zaghouan km 12"),
        new("city", "City", "Ville", Required: true, Example: "Zaghouan"),
        new("region", "Region", "Gouvernorat / région", Required: false),
        new("postalCode", "Postal code", "Code postal", Required: false),
        new("country", "Country", "Pays", Required: true, Example: "TN"),
        new("latitude", "Latitude", "Latitude", Required: true, ImportColumnType.Number, Example: "36.4029"),
        new("longitude", "Longitude", "Longitude", Required: true, ImportColumnType.Number, Example: "10.1429"),
        new("timeZone", "Time zone", "Fuseau horaire", Required: false, Example: "Africa/Tunis"),
        new("currency", "Currency", "Devise", Required: false, List: "currencies"),
    ];

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["types"] = [.. SiteRules.TypeCodes],
            ["currencies"] = [.. SupportedCurrency.SupportedCodes.Order(StringComparer.Ordinal)],
        });

    public async Task<ImportValidation> ValidateAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) =>
        (await PrepareAsync(rows, cancellationToken).ConfigureAwait(false)).Validation;

    public async Task<ImportCommit> CommitAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(rows, cancellationToken).ConfigureAwait(false);
        if (prepared.Validation.HasErrors)
        {
            return new ImportCommit(prepared.Validation, 0, 0, 0);
        }

        foreach (var details in prepared.Creates)
        {
            store.Add(Site.Create(clock, Tenant, details, caller.Subject));
        }

        // A new clock or address moves the stored shift instances, as a change on screen does.
        foreach (var site in prepared.Changed)
        {
            await shifts.ReconcileAsync(site, cancellationToken).ConfigureAwait(false);
        }

        return await store.TrySaveAllAsync(cancellationToken).ConfigureAwait(false)
            ? ImportCommits.Of(prepared.Validation)
            : ImportCommits.DataChanged(prepared.Validation);
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Sites need a tenant.");

    private async Task<(ImportValidation Validation, List<SiteDetails> Creates, List<Site> Changed)> PrepareAsync(
        IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var companyZone = (await tenants.FindAsync(Tenant, cancellationToken).ConfigureAwait(false))?.TimeZone;
        var issues = ImportCodes.Duplicates(rows, "code", CodeOf).ToList();
        var duplicated = issues.Select(i => i.Row).ToHashSet();
        var codes = rows.Select(CodeOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var existing = (await store.FindByCodesAsync(codes, cancellationToken).ConfigureAwait(false)).ToDictionary(s => s.Code, StringComparer.Ordinal);

        var plans = new List<ImportRowPlan>();
        var creates = new List<SiteDetails>();
        var changed = new List<Site>();
        foreach (var row in rows)
        {
            var rowIssues = Columns.Where(c => c.Required && row[c.Key] is null).Select(c => ImportIssue.Error(row, c.Key, ImportCodes.ValueRequired)).ToList();
            var latitude = Number(row, "latitude", rowIssues);
            var longitude = Number(row, "longitude", rowIssues);

            SiteDetails? details = null;
            if (rowIssues.Count == 0)
            {
                var checkedDetails = SiteRules.Validate(row["code"], row["name"], row["type"]?.Trim().ToLowerInvariant(), row["address"], row["city"], row["region"],
                    row["postalCode"], row["country"], latitude!.Value, longitude!.Value, boundary: null, row["timeZone"]?.Trim() ?? companyZone, row["currency"]);
                if (checkedDetails.IsSuccess)
                {
                    details = checkedDetails.Value;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, ColumnOfError.GetValueOrDefault(checkedDetails.Error!.Code, "code"), checkedDetails.Error!));
                }
            }

            var site = details is null ? null : existing.GetValueOrDefault(details.Code);
            if (site is { Status: SiteStatus.Archived })
            {
                rowIssues.Add(ImportIssue.Error(row, "code", Archived));
            }

            issues.AddRange(rowIssues);
            if (rowIssues.Count > 0 || duplicated.Contains(row.Number) || details is null)
            {
                continue;
            }

            if (site is null)
            {
                creates.Add(details);
                plans.Add(new ImportRowPlan(row.Number, details.Code, ImportAction.Create));
                continue;
            }

            var change = site.Change(clock, details with { Boundary = site.Boundary }, caller.Subject);
            if (change.Value)
            {
                changed.Add(site);
            }

            plans.Add(new ImportRowPlan(row.Number, site.Code, change.Value ? ImportAction.Update : ImportAction.Unchanged));
        }

        if (creates.Count > 0 && await limits.CheckCreateAsync(Metrics.Sites, creates.Count, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            issues.Add(ImportIssue.ForFile(refused));
        }

        return (new ImportValidation(issues, plans), creates, changed);
    }

    private static double? Number(ImportRow row, string column, List<ImportIssue> issues)
    {
        if (row[column] is not { } text)
        {
            return null;
        }

        if (ImportValues.TryDecimal(text, out var value))
        {
            return (double)value;
        }

        issues.Add(ImportIssue.Error(row, column, ImportCodes.NumberInvalid));
        return null;
    }

    // The same normalisation as the rules (trimmed, upper case), so duplicates and existing sites are found by it.
    private static string? CodeOf(ImportRow row) => row["code"]?.Trim().ToUpperInvariant() is { Length: > 0 } code ? code : null;
}
