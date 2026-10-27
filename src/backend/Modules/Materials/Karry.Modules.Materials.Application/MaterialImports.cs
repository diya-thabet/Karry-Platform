using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Materials.Domain;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Materials.Application;

/// <summary>
/// The <c>materials</c> import (US-P1-ORG-03, ADR-0027): one row per material, keyed by its code, with its densities.
/// A new code creates the material with these densities from <c>effectiveFrom</c> (default: the company's today). A
/// known code changes its name and kind; different densities become a version from a future <c>effectiveFrom</c> (past
/// quantities keep theirs, as on screen), so without one they are an error on the row. Empty densities leave a known
/// material's versions as they are. An archived material is an error. Validation prepares the changes on the loaded
/// materials and never saves them; the commit prepares them again and saves the whole file in one transaction.
/// </summary>
public sealed class MaterialImport(IMaterialStore store, MaterialOperations materials, ICallerAccess caller, ITenantContext tenant, TimeProvider clock) : IImportTarget
{
    private static readonly Dictionary<string, string> ColumnOfError = new(StringComparer.Ordinal)
    {
        [MaterialRules.CodeInvalid.Code] = "code",
        [MaterialRules.NameInvalid.Code] = "name",
        [MaterialRules.KindInvalid.Code] = "kind",
        [MaterialRules.DensityRequired.Code] = "bank",
        [MaterialRules.MoistureInvalid.Code] = "moisture",
        ["materials.swell_invalid"] = "loose",
        ["materials.date_in_past"] = "effectiveFrom",
        [MaterialRules.Archived.Code] = "code",
    };

    public string Kind => "materials";

    public string Permission => MaterialsPermissions.CatalogManage;

    public IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new("code", "Code", "Code", Required: true, Example: "G-8/16"),
        new("name", "Name", "Nom", Required: true, Example: "Gravier 8/16"),
        new("kind", "Kind", "Nature", Required: true, List: "kinds", Example: "product"),
        new("bank", "Bank density (t/m³)", "Densité en place (t/m³)", Required: false, ImportColumnType.Number, Example: "2.65"),
        new("loose", "Loose density (t/m³)", "Densité foisonnée (t/m³)", Required: false, ImportColumnType.Number, Example: "1.6"),
        new("compacted", "Compacted density (t/m³)", "Densité compactée (t/m³)", Required: false, ImportColumnType.Number),
        new("moisture", "Moisture (fraction)", "Humidité (fraction)", Required: false, ImportColumnType.Number, Example: "0.03"),
        new("effectiveFrom", "Densities from", "Densités à partir du", Required: false, ImportColumnType.Date),
    ];

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["kinds"] = [MaterialRules.KindCode(MaterialKind.RawRock), MaterialRules.KindCode(MaterialKind.Product)],
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

        foreach (var (code, name, kind, version) in prepared.Creates)
        {
            store.Add(Material.Create(clock, Tenant, code, name, kind, version, caller.Subject));
        }

        return await store.TrySaveAllAsync(cancellationToken).ConfigureAwait(false)
            ? ImportCommits.Of(prepared.Validation)
            : ImportCommits.DataChanged(prepared.Validation);
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Materials need a tenant.");

    private async Task<(ImportValidation Validation, List<(string Code, string Name, MaterialKind Kind, DensityVersion Version)> Creates)> PrepareAsync(
        IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var today = await materials.TodayAsync(cancellationToken).ConfigureAwait(false);
        var issues = ImportCodes.Duplicates(rows, "code", CodeOf).ToList();
        var duplicated = issues.Select(i => i.Row).ToHashSet();
        var codes = rows.Select(CodeOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var existing = (await store.FindByCodesAsync(codes, cancellationToken).ConfigureAwait(false)).ToDictionary(m => m.Code, StringComparer.Ordinal);

        var plans = new List<ImportRowPlan>();
        var creates = new List<(string, string, MaterialKind, DensityVersion)>();
        foreach (var row in rows)
        {
            var rowIssues = Columns.Where(c => c.Required && row[c.Key] is null).Select(c => ImportIssue.Error(row, c.Key, ImportCodes.ValueRequired)).ToList();
            var bank = Number(row, "bank", rowIssues);
            var loose = Number(row, "loose", rowIssues);
            var compacted = Number(row, "compacted", rowIssues);
            var moisture = Number(row, "moisture", rowIssues);
            DateOnly? from = null;
            if (row["effectiveFrom"] is { } dateText)
            {
                if (ImportValues.TryDate(dateText, out var parsed))
                {
                    from = parsed;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "effectiveFrom", ImportCodes.DateInvalid));
                }
            }

            var code = Check(row, "code", MaterialRules.Code(row["code"]), rowIssues);
            var name = Check(row, "name", MaterialRules.Name(row["name"]), rowIssues);
            MaterialKind? kind = null;
            if (row["kind"] is { } kindText)
            {
                var checkedKind = MaterialRules.Kind(kindText.Trim().ToLowerInvariant());
                if (checkedKind.IsSuccess)
                {
                    kind = checkedKind.Value;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "kind", checkedKind.Error!));
                }
            }

            var material = code is null ? null : existing.GetValueOrDefault(code);
            if (material is { Status: MaterialStatus.Archived })
            {
                rowIssues.Add(ImportIssue.Error(row, "code", MaterialRules.Archived));
            }

            // A known material without densities in the row keeps its versions.
            var densitiesGiven = bank is not null || loose is not null || compacted is not null || moisture is not null;
            DensityVersion? version = null;
            if (rowIssues.Count == 0 && (material is null || densitiesGiven))
            {
                version = Check(row, "bank", MaterialRules.Version(from ?? today, bank, loose, compacted, moisture), rowIssues);
            }

            issues.AddRange(rowIssues);
            if (rowIssues.Count > 0 || duplicated.Contains(row.Number) || code is null || name is null || kind is null)
            {
                continue;
            }

            if (material is null)
            {
                creates.Add((code, name, kind.Value, version!));
                plans.Add(new ImportRowPlan(row.Number, code, ImportAction.Create));
                continue;
            }

            var changed = material.Change(clock, name, kind.Value, caller.Subject);
            // The densities in force on that date already: nothing to set (the date alone is no change).
            var current = version is null ? null : material.VersionOn(version.EffectiveFrom);
            if (version is not null && (current is null || current with { EffectiveFrom = version.EffectiveFrom } != version))
            {
                var set = material.SetVersion(clock, version, today, caller.Subject);
                if (!set.IsSuccess)
                {
                    issues.Add(ImportIssue.Error(row, ColumnOf(set.Error!), set.Error!));
                    continue;
                }

                changed |= set.Value;
            }

            plans.Add(new ImportRowPlan(row.Number, code, changed ? ImportAction.Update : ImportAction.Unchanged));
        }

        return (new ImportValidation(issues, plans), creates);
    }

    private static T? Check<T>(ImportRow row, string column, Result<T> result, List<ImportIssue> issues)
        where T : class
    {
        if (result.IsSuccess)
        {
            return result.Value;
        }

        issues.Add(ImportIssue.Error(row, ColumnOf(result.Error!, column), result.Error!));
        return default;
    }

    private static decimal? Number(ImportRow row, string column, List<ImportIssue> issues)
    {
        if (row[column] is not { } text)
        {
            return null;
        }

        if (ImportValues.TryDecimal(text, out var value))
        {
            return value;
        }

        issues.Add(ImportIssue.Error(row, column, ImportCodes.NumberInvalid));
        return null;
    }

    // A density error names its state (bank, loose, compacted), which is also its column.
    private static string ColumnOf(Error error, string fallback = "code") =>
        error.Extensions.TryGetValue("state", out var state) ? (string)state : ColumnOfError.GetValueOrDefault(error.Code, fallback);

    private static string? CodeOf(ImportRow row) => MaterialRules.Code(row["code"]) is { IsSuccess: true } code ? code.Value : null;
}
