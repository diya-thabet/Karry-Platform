using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Weighbridge.Domain;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Weighbridge.Application;

/// <summary>
/// The <c>trucks</c> import (US-P3-SAL-04, ADR-0027): one row per truck, keyed by its plate however it is written
/// (spaces, dashes and case ignored, TC-P3-WB-016), naming its hauler by name. A new plate registers an active truck; a
/// known plate changes its details. An empty cell keeps what the truck has (a file never erases a registered tare by
/// leaving a column out); the three tare cells go together. An archived truck is an error (restore it on screen first).
/// An unverified truck stays unverified: verifying is someone else's decision (<c>weighbridge.trucks.verify</c>), and
/// the weighmaster, who may import, does not clear the flag of a truck met at the desk. Nothing is saved unless every
/// row is valid.
/// </summary>
public sealed class TruckImport(
    ITruckStore store, IHaulerStore haulers, ITenantDirectory tenants, ICallerAccess caller, ITenantContext tenant, TimeProvider clock) : IImportTarget
{
    public static readonly Error Archived = Error.Conflict("weighbridge.truck_archived", "This truck is archived: restore it before changing it.");

    public string Kind => "trucks";

    public string Permission => WeighbridgePermissions.TrucksManage;

    public IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new("plate", "Plate", "Plaque", Required: true, Example: "123 TU 4567"),
        new("hauler", "Hauler", "Transporteur", Required: false, List: "haulers", Example: "Transports Ben Salah"),
        new("tareKg", "Registered tare (kg)", "Tare enregistrée (kg)", Required: false, Example: "14200"),
        new("tareOn", "Tare date", "Date de la tare", Required: false, Example: "2026-11-02"),
        new("tareMethod", "Tare method", "Méthode de la tare", Required: false, List: "tareMethods", Example: "weighed"),
        new("maxPayloadKg", "Legal payload (kg)", "Charge utile légale (kg)", Required: false, Example: "26000"),
        new("axles", "Axles", "Essieux", Required: false, Example: "2+3"),
    ];

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken) =>
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["haulers"] = [.. (await ActiveHaulersAsync(cancellationToken).ConfigureAwait(false)).Values.Select(h => h.Name).Order(StringComparer.Ordinal)],
            ["tareMethods"] = [TruckRules.MethodCode(TareMethod.Weighed), TruckRules.MethodCode(TareMethod.Declared)],
        };

    public async Task<ImportValidation> ValidateAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) =>
        (await PrepareAsync(rows, cancellationToken).ConfigureAwait(false)).Validation;

    public async Task<ImportCommit> CommitAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(rows, cancellationToken).ConfigureAwait(false);
        if (prepared.Validation.HasErrors)
        {
            return new ImportCommit(prepared.Validation, 0, 0, 0);
        }

        foreach (var (plate, details) in prepared.Registrations)
        {
            store.Add(Truck.Register(clock, Tenant, plate, details, caller.Subject));
        }

        return await store.TrySaveAllAsync(cancellationToken).ConfigureAwait(false)
            ? ImportCommits.Of(prepared.Validation)
            : ImportCommits.DataChanged(prepared.Validation);
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Weighbridge needs a tenant.");

    /// <summary>The company's active haulers, by name key (a name matches however it is written).</summary>
    private async Task<Dictionary<string, Hauler>> ActiveHaulersAsync(CancellationToken cancellationToken) =>
        (await haulers.ListAsync(includeArchived: false, cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.NameKey, StringComparer.Ordinal);

    private async Task<(ImportValidation Validation, List<(string Plate, TruckDetails Details)> Registrations)> PrepareAsync(
        IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var issues = ImportCodes.Duplicates(rows, "plate", KeyOf).ToList();
        var duplicated = issues.Select(i => i.Row).ToHashSet();
        var keys = rows.Select(KeyOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var existing = (await store.FindByKeysAsync(keys, cancellationToken).ConfigureAwait(false)).ToDictionary(t => t.PlateKey, StringComparer.Ordinal);
        var active = await ActiveHaulersAsync(cancellationToken).ConfigureAwait(false);
        var today = await CompanyDay.TodayAsync(tenants, Tenant, clock, cancellationToken).ConfigureAwait(false);

        var plans = new List<ImportRowPlan>();
        var registrations = new List<(string, TruckDetails)>();
        foreach (var row in rows)
        {
            var rowIssues = new List<ImportIssue>();
            var plate = TruckRules.Plate(row["plate"]);
            if (row["plate"] is null)
            {
                rowIssues.Add(ImportIssue.Error(row, "plate", ImportCodes.ValueRequired));
            }
            else if (!plate.IsSuccess)
            {
                rowIssues.Add(ImportIssue.Error(row, "plate", plate.Error!));
            }

            var truck = plate.IsSuccess ? existing.GetValueOrDefault(TruckRules.Key(plate.Value)) : null;
            if (truck is { Status: TruckStatus.Archived })
            {
                rowIssues.Add(ImportIssue.Error(row, "plate", Archived));
            }

            var details = Details(row, truck?.Details, active, today, rowIssues);
            issues.AddRange(rowIssues);
            if (rowIssues.Count > 0 || duplicated.Contains(row.Number) || details is null)
            {
                continue;
            }

            if (truck is null)
            {
                registrations.Add((plate.Value, details));
                plans.Add(new ImportRowPlan(row.Number, plate.Value, ImportAction.Create));
                continue;
            }

            var changed = truck.Change(clock, details, caller.Subject);
            plans.Add(new ImportRowPlan(row.Number, truck.Plate, changed ? ImportAction.Update : ImportAction.Unchanged));
        }

        return (new ImportValidation(issues, plans), registrations);
    }

    /// <summary>The row's details over <paramref name="current"/> (empty cells keep it); null with the row's issues added.</summary>
    private static TruckDetails? Details(ImportRow row, TruckDetails? current, Dictionary<string, Hauler> active, DateOnly today, List<ImportIssue> issues)
    {
        var start = issues.Count;
        var haulerId = current?.HaulerId;
        if (row["hauler"] is { } name)
        {
            if (active.TryGetValue(TruckRules.Key(name), out var hauler))
            {
                haulerId = hauler.Id.Value;
            }
            else
            {
                issues.Add(ImportIssue.Error(row, "hauler", TruckOperations.HaulerUnknown));
            }
        }

        var tare = current?.Tare;
        if (row["tareKg"] is not null || row["tareOn"] is not null || row["tareMethod"] is not null)
        {
            decimal? kg = Number(row, "tareKg", issues);
            DateOnly? on = null;
            if (row["tareOn"] is { } day)
            {
                if (ImportValues.TryDate(day, out var date))
                {
                    on = date;
                }
                else
                {
                    issues.Add(ImportIssue.Error(row, "tareOn", ImportCodes.DateInvalid));
                }
            }

            var checkedTare = TruckRules.Tare(kg, on, row["tareMethod"], today);
            if (issues.Count == start && !checkedTare.IsSuccess)
            {
                // The cell that is wrong: the mass, else the day (missing or in the future), else the method.
                var column = kg is null || !TruckRules.Payload(kg).IsSuccess ? "tareKg" : on is null || on > today ? "tareOn" : "tareMethod";
                issues.Add(ImportIssue.Error(row, column, checkedTare.Error!));
            }

            tare = checkedTare.IsSuccess ? checkedTare.Value : null;
        }

        var payload = current?.MaxPayloadKg;
        if (Number(row, "maxPayloadKg", issues) is { } kgPayload)
        {
            var checkedPayload = TruckRules.Payload(kgPayload);
            if (checkedPayload.IsSuccess)
            {
                payload = checkedPayload.Value;
            }
            else
            {
                issues.Add(ImportIssue.Error(row, "maxPayloadKg", checkedPayload.Error!));
            }
        }

        var axles = current?.Axles;
        if (row["axles"] is { } axleText)
        {
            var checkedAxles = TruckRules.Axles(axleText);
            if (checkedAxles.IsSuccess)
            {
                axles = checkedAxles.Value;
            }
            else
            {
                issues.Add(ImportIssue.Error(row, "axles", checkedAxles.Error!));
            }
        }

        return issues.Count == start ? new TruckDetails(haulerId, tare, payload, axles) : null;
    }

    /// <summary>The number in the cell, null when it is empty; an issue when it is not a number.</summary>
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

    private static string? KeyOf(ImportRow row) => TruckRules.Plate(row["plate"]) is { IsSuccess: true } plate ? TruckRules.Key(plate.Value) : null;
}
