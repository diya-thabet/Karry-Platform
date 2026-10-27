using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Domain;
using Karry.Modules.Organization.Contracts;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Assets.Application;

/// <summary>
/// The <c>assets</c> import (US-P1-ORG-03, ADR-0027): one row per machine, keyed by its code. A new code registers the
/// asset with its type's default meters, as on screen; a known code changes its details and moves it to the row's site;
/// an identical row is left as it is. The same rules and ledger events as through the API, with the importer's sites:
/// a site the importer does not manage is an error on that row. Validation prepares the changes on the loaded assets and
/// never saves them; the commit prepares them again and saves the whole file in one transaction.
/// </summary>
public sealed class AssetImport(
    IAssetStore store, IMeterStore meters, ISiteDirectory sites, AssetOperations assets, ILimitGuard limits, ICallerAccess caller, ITenantContext tenant,
    TimeProvider clock) : IImportTarget
{
    private static readonly Dictionary<string, string> ColumnOfError = new(StringComparer.Ordinal)
    {
        [AssetRules.NameInvalid.Code] = "name",
        [AssetRules.TypeInvalid.Code] = "type",
        ["assets.year_invalid"] = "year",
        ["assets.acquired_on_invalid"] = "acquiredOn",
    };

    public string Kind => "assets";

    public string Permission => AssetsPermissions.RegistryManage;

    public IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new("code", "Code", "Code", Required: true, Example: "PEL-01"),
        new("name", "Name", "Nom", Required: true, Example: "Pelle CAT 336"),
        new("type", "Type", "Type", Required: true, List: "types", Example: "excavator"),
        new("site", "Site", "Site", Required: true, List: "sites"),
        new("make", "Make", "Marque", Required: false, Example: "Caterpillar"),
        new("model", "Model", "Modèle", Required: false, Example: "336"),
        new("serialNumber", "Serial number", "N° de série", Required: false),
        new("plate", "Plate", "Immatriculation", Required: false),
        new("year", "Year", "Année", Required: false, ImportColumnType.Number, Example: "2019"),
        new("acquiredOn", "Acquired on", "Date d'acquisition", Required: false, ImportColumnType.Date, Example: "2019-03-15"),
    ];

    /// <summary>The type codes, and the codes of the active sites the importer manages.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken) =>
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["types"] = [.. AssetTypes.All.Select(t => t.Code)],
            ["sites"] = [.. (await ManagedSitesAsync(cancellationToken).ConfigureAwait(false)).Keys.Order(StringComparer.Ordinal)],
        };

    public async Task<ImportValidation> ValidateAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) =>
        (await PrepareAsync(rows, cancellationToken).ConfigureAwait(false)).Validation;

    public async Task<ImportCommit> CommitAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(rows, cancellationToken).ConfigureAwait(false);
        return await ImportSaving.SaveAsync(prepared.Validation, prepared.Create, store, cancellationToken).ConfigureAwait(false);
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Assets need a tenant.");

    private async Task<(ImportValidation Validation, Action Create)> PrepareAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var today = await assets.TodayAsync(cancellationToken).ConfigureAwait(false);
        var managedSites = await ManagedSitesAsync(cancellationToken).ConfigureAwait(false);
        var manage = caller.Current.ScopeOf(AssetsPermissions.RegistryManage);
        var issues = ImportCodes.Duplicates(rows, "code", CodeOf).ToList();
        var duplicated = issues.Select(i => i.Row).ToHashSet();
        var codes = rows.Select(CodeOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var existing = (await store.FindByCodesAsync(codes, cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.Code, StringComparer.Ordinal);

        var plans = new List<ImportRowPlan>();
        var creates = new List<(string Code, AssetDetails Details, Guid SiteId)>();
        foreach (var row in rows)
        {
            var rowIssues = new List<ImportIssue>();
            foreach (var column in Columns.Where(c => c.Required && row[c.Key] is null))
            {
                rowIssues.Add(ImportIssue.Error(row, column.Key, ImportCodes.ValueRequired));
            }

            string? code = null;
            if (row["code"] is not null)
            {
                var checkedCode = AssetRules.Code(row["code"]);
                if (checkedCode.IsSuccess)
                {
                    code = checkedCode.Value;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "code", checkedCode.Error!));
                }
            }

            Guid? siteId = null;
            if (row["site"] is { } siteCode)
            {
                if (managedSites.TryGetValue(siteCode.Trim(), out var site))
                {
                    siteId = site;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "site", AssetOperations.SiteInvalid));
                }
            }

            int? year = null;
            if (row["year"] is { } yearText)
            {
                if (ImportValues.TryInteger(yearText, out var parsed))
                {
                    year = parsed;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "year", ImportCodes.NumberInvalid));
                }
            }

            DateOnly? acquiredOn = null;
            if (row["acquiredOn"] is { } dateText)
            {
                if (ImportValues.TryDate(dateText, out var parsed))
                {
                    acquiredOn = parsed;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "acquiredOn", ImportCodes.DateInvalid));
                }
            }

            AssetDetails? details = null;
            if (row["name"] is not null && row["type"] is not null)
            {
                var checkedDetails = AssetRules.Details(row["name"], row["type"], row["make"], row["model"], row["serialNumber"], row["plate"], year, acquiredOn, today);
                if (checkedDetails.IsSuccess)
                {
                    details = checkedDetails.Value;
                }
                else if (!rowIssues.Any(i => i.Column is "year" or "acquiredOn"))
                {
                    rowIssues.Add(ImportIssue.Error(row, ColumnOf(checkedDetails.Error!), checkedDetails.Error!));
                }
            }

            var asset = code is null ? null : existing.GetValueOrDefault(code);
            if (asset is { Status: AssetStatus.Disposed })
            {
                rowIssues.Add(ImportIssue.Error(row, "code", AssetRules.Disposed));
            }
            else if (asset is not null && !manage.CoversSite(asset.SiteId))
            {
                rowIssues.Add(ImportIssue.Error(row, "site", AssetOperations.SiteForbidden));
            }

            issues.AddRange(rowIssues);
            if (rowIssues.Count > 0 || duplicated.Contains(row.Number) || code is null || details is null || siteId is null)
            {
                continue;
            }

            if (asset is null)
            {
                creates.Add((code, details, siteId.Value));
                plans.Add(new ImportRowPlan(row.Number, code, ImportAction.Create));
                continue;
            }

            var changed = asset.Change(clock, details, caller.Subject).Value | asset.MoveTo(clock, siteId.Value, caller.Subject).Value;
            plans.Add(new ImportRowPlan(row.Number, asset.Code, changed ? ImportAction.Update : ImportAction.Unchanged));
        }

        if (creates.Count > 0 && await limits.CheckCreateAsync(Metrics.Assets, creates.Count, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            issues.Add(ImportIssue.ForFile(refused));
        }

        void Create()
        {
            foreach (var (code, details, siteId) in creates)
            {
                var asset = Asset.Register(clock, Tenant, code, details, siteId, caller.Subject);
                store.Add(asset);
                foreach (var kind in AssetTypes.Find(details.Type)!.DefaultMeters)
                {
                    meters.Add(Meter.Add(clock, Tenant, asset.Id, kind, 0m, details.AcquiredOn ?? today, caller.Subject));
                }
            }
        }

        return (new ImportValidation(issues, plans), Create);
    }

    private static string? CodeOf(ImportRow row) => AssetRules.Code(row["code"]) is { IsSuccess: true } code ? code.Value : null;

    private static string ColumnOf(Error error) =>
        ColumnOfError.TryGetValue(error.Code, out var column) ? column : error.Extensions.TryGetValue("field", out var field) ? (string)field : "code";

    /// <summary>Code → ID of the active sites the importer may put assets on (codes compared without case).</summary>
    private async Task<Dictionary<string, Guid>> ManagedSitesAsync(CancellationToken cancellationToken)
    {
        var manage = caller.Current.ScopeOf(AssetsPermissions.RegistryManage);
        return (await sites.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(s => !s.Archived && manage.CoversSite(s.Id))
            .ToDictionary(s => s.Code, s => s.Id, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The <c>meters</c> import (US-P1-ORG-03 AC1, "initial readings"): one row per meter of an existing asset, keyed by
/// (asset code, meter). When the asset has a working meter of that kind, the row is a reading of it (the meter never
/// goes back, as on screen; the same value is left as it is); otherwise it adds the meter with the row's value as its
/// first reading. Assets on sites the importer does not manage are errors on their rows.
/// </summary>
public sealed class MeterImport(IAssetStore store, IMeterStore meters, AssetOperations assets, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
    : IImportTarget
{
    private static readonly Dictionary<string, string> ColumnOfError = new(StringComparer.Ordinal)
    {
        [MeterRules.KindInvalid.Code] = "meter",
        [MeterRules.NotCounting.Code] = "meter",
        [MeterRules.ValueInvalid.Code] = "reading",
        ["assets.reading_decreased"] = "reading",
        [MeterRules.ReadInFuture.Code] = "readOn",
        ["assets.reading_before_last"] = "readOn",
        ["assets.installed_on_invalid"] = "readOn",
    };

    public string Kind => "meters";

    public string Permission => AssetsPermissions.RegistryManage;

    public IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new("asset", "Asset code", "Code engin", Required: true, Example: "PEL-01"),
        new("meter", "Meter", "Compteur", Required: true, List: "meters", Example: "hours"),
        new("reading", "Reading", "Relevé", Required: true, ImportColumnType.Number, Example: "4250.5"),
        new("readOn", "Read on", "Date du relevé", Required: false, ImportColumnType.Date, Example: "2026-10-01"),
    ];

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["meters"] = [AssetRules.MeterKindCode(MeterKind.Hours), AssetRules.MeterKindCode(MeterKind.Km)],
        });

    public async Task<ImportValidation> ValidateAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) =>
        (await PrepareAsync(rows, cancellationToken).ConfigureAwait(false)).Validation;

    public async Task<ImportCommit> CommitAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(rows, cancellationToken).ConfigureAwait(false);
        return await ImportSaving.SaveAsync(prepared.Validation, prepared.Create, store, cancellationToken).ConfigureAwait(false);
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Assets need a tenant.");

    private async Task<(ImportValidation Validation, Action Create)> PrepareAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var today = await assets.TodayAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var manage = caller.Current.ScopeOf(AssetsPermissions.RegistryManage);
        var issues = ImportCodes.Duplicates(rows, "asset", KeyOf).ToList();
        var duplicated = issues.Select(i => i.Row).ToHashSet();
        var codes = rows.Select(r => AssetRules.Code(r["asset"])).Where(c => c.IsSuccess).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        var byCode = (await store.FindByCodesAsync(codes, cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.Code, StringComparer.Ordinal);
        var loaded = await meters.ForAssetsAsync([.. byCode.Values.Select(a => a.Id)], cancellationToken).ConfigureAwait(false);

        var plans = new List<ImportRowPlan>();
        var creates = new List<(AssetId Asset, MeterKind Kind, decimal Initial, DateOnly On)>();
        foreach (var row in rows)
        {
            var rowIssues = new List<ImportIssue>();
            foreach (var column in Columns.Where(c => c.Required && row[c.Key] is null))
            {
                rowIssues.Add(ImportIssue.Error(row, column.Key, ImportCodes.ValueRequired));
            }

            Asset? asset = null;
            if (row["asset"] is not null)
            {
                var code = AssetRules.Code(row["asset"]);
                asset = code.IsSuccess ? byCode.GetValueOrDefault(code.Value) : null;
                if (asset is null || !manage.CoversSite(asset.SiteId))
                {
                    // An asset on a site the importer does not manage answers as unknown, as through the API.
                    rowIssues.Add(ImportIssue.Error(row, "asset", AssetOperations.NotFound));
                }
                else if (asset.Status == AssetStatus.Disposed)
                {
                    rowIssues.Add(ImportIssue.Error(row, "asset", AssetRules.Disposed));
                }
            }

            MeterKind? kind = null;
            if (row["meter"] is { } meterText)
            {
                var checkedKind = MeterRules.Kind(meterText.Trim().ToLowerInvariant());
                if (checkedKind.IsSuccess)
                {
                    kind = checkedKind.Value;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "meter", checkedKind.Error!));
                }
            }

            decimal? value = null;
            if (row["reading"] is { } readingText)
            {
                if (ImportValues.TryDecimal(readingText, out var parsed))
                {
                    value = parsed;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "reading", ImportCodes.NumberInvalid));
                }
            }

            DateOnly? readOn = null;
            if (row["readOn"] is { } dateText)
            {
                if (ImportValues.TryDate(dateText, out var parsed))
                {
                    readOn = parsed;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "readOn", ImportCodes.DateInvalid));
                }
            }

            if (rowIssues.Count > 0 || duplicated.Contains(row.Number) || asset is null || kind is not { } meterKind || value is not { } reading)
            {
                issues.AddRange(rowIssues);
                continue;
            }

            var key = $"{asset.Code}/{AssetRules.MeterKindCode(meterKind)}";
            var meter = loaded.FirstOrDefault(m => m.AssetId == asset.Id && m.Kind == meterKind && m.Status == MeterStatus.Ok);
            if (meter is null)
            {
                var added = MeterOperations.Validate(new MeterInput(AssetRules.MeterKindCode(meterKind), reading, readOn), today);
                if (!added.IsSuccess)
                {
                    issues.Add(ImportIssue.Error(row, ColumnOf(added.Error!), added.Error!));
                    continue;
                }

                creates.Add((asset.Id, added.Value.Kind, added.Value.Initial, added.Value.On));
                plans.Add(new ImportRowPlan(row.Number, key, ImportAction.Create));
            }
            else if (reading == meter.LastValue)
            {
                plans.Add(new ImportRowPlan(row.Number, key, ImportAction.Unchanged));
            }
            else
            {
                // A reading dated today is taken now; an earlier date, at noon that day.
                var readAt = readOn is not { } on || on == today ? now : new DateTimeOffset(on, new TimeOnly(12, 0), TimeSpan.Zero);
                var recorded = meter.Record(clock, reading, readAt, caller.Subject);
                if (!recorded.IsSuccess)
                {
                    issues.Add(ImportIssue.Error(row, ColumnOf(recorded.Error!), recorded.Error!));
                    continue;
                }

                meters.Stage(meter);
                plans.Add(new ImportRowPlan(row.Number, key, ImportAction.Update));
            }
        }

        void Create()
        {
            foreach (var (asset, kind, initial, on) in creates)
            {
                meters.Add(Meter.Add(clock, Tenant, asset, kind, initial, on, caller.Subject));
            }
        }

        return (new ImportValidation(issues, plans), Create);
    }

    private static string? KeyOf(ImportRow row) =>
        AssetRules.Code(row["asset"]) is { IsSuccess: true } code && row["meter"] is { } meter ? $"{code.Value}/{meter.Trim().ToLowerInvariant()}" : null;

    private static string ColumnOf(Error error) => ColumnOfError.GetValueOrDefault(error.Code, "reading");
}

/// <summary>The end of an Assets import commit: nothing when an error remains, else the whole file in one save.</summary>
internal static class ImportSaving
{
    public static async Task<ImportCommit> SaveAsync(ImportValidation validation, Action create, IAssetStore store, CancellationToken cancellationToken)
    {
        if (validation.HasErrors)
        {
            return new ImportCommit(validation, 0, 0, 0);
        }

        create();
        if (!await store.TrySaveAllAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ImportCommit(validation with { Issues = [.. validation.Issues, new ImportIssue(0, null, null, ImportCodes.DataChanged, ImportSeverity.Error)] }, 0, 0, 0);
        }

        return new ImportCommit(
            validation,
            validation.Plans.Count(p => p.Action == ImportAction.Create),
            validation.Plans.Count(p => p.Action == ImportAction.Update),
            validation.Plans.Count(p => p.Action == ImportAction.Unchanged));
    }
}
