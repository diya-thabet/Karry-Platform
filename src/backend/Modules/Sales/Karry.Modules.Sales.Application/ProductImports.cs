using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Materials.Contracts;
using Karry.Modules.Sales.Domain;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Sales.Application;

/// <summary>
/// The <c>products</c> import (US-P3-SAL-02 AC1, ADR-0027): one row per product, keyed by its code, naming its material
/// by the material's code (a sold fraction of the company, so materials are imported first). A new code creates the
/// product; a known code changes its name, material and sale unit; an archived product is an error (restore it on
/// screen first). Nothing is saved unless every row is valid.
/// </summary>
public sealed class ProductImport(IProductStore store, IMaterialDirectory materials, ICallerAccess caller, ITenantContext tenant, TimeProvider clock) : IImportTarget
{
    public static readonly Error Archived = Error.Conflict("sales.product_archived", "This product is archived: restore it before changing it.");

    private static readonly Dictionary<string, string> ColumnOfError = new(StringComparer.Ordinal)
    {
        [ProductRules.CodeInvalid.Code] = "code",
        [ProductRules.NameInvalid.Code] = "name",
        [ProductRules.MaterialRequired.Code] = "material",
        [ProductRules.SaleUnitInvalid.Code] = "saleUnit",
    };

    public string Kind => "products";

    public string Permission => SalesPermissions.ProductsManage;

    public IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new("code", "Code", "Code", Required: true, Example: "G-8/16"),
        new("name", "Name", "Nom", Required: true, Example: "Gravier 8/16"),
        new("material", "Material (code)", "Matériau (code)", Required: true, List: "materials", Example: "G-8/16"),
        new("saleUnit", "Sale unit", "Unité de vente", Required: false, List: "units", Example: "t"),
    ];

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken) =>
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["materials"] = [.. (await SoldAsync(cancellationToken).ConfigureAwait(false)).Keys.Order(StringComparer.Ordinal)],
            ["units"] = [CustomerRules.BasisCode(InvoicingBasis.Tonnes), CustomerRules.BasisCode(InvoicingBasis.CubicMetresLoose)],
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

        foreach (var (code, details) in prepared.Creates)
        {
            store.Add(Product.Create(clock, Tenant, code, details, caller.Subject));
        }

        return await store.TrySaveAllAsync(cancellationToken).ConfigureAwait(false)
            ? ImportCommits.Of(prepared.Validation)
            : ImportCommits.DataChanged(prepared.Validation);
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Sales needs a tenant.");

    /// <summary>The company's sold fractions that are not archived, by material code.</summary>
    private async Task<Dictionary<string, Guid>> SoldAsync(CancellationToken cancellationToken) =>
        (await materials.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(m => m is { IsProduct: true, Archived: false }).ToDictionary(m => m.Code, m => m.Id, StringComparer.OrdinalIgnoreCase);

    private async Task<(ImportValidation Validation, List<(string Code, ProductDetails Details)> Creates)> PrepareAsync(
        IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var issues = ImportCodes.Duplicates(rows, "code", CodeOf).ToList();
        var duplicated = issues.Select(i => i.Row).ToHashSet();
        var codes = rows.Select(CodeOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var existing = (await store.FindByCodesAsync(codes, cancellationToken).ConfigureAwait(false)).ToDictionary(p => p.Code, StringComparer.Ordinal);
        var sold = await SoldAsync(cancellationToken).ConfigureAwait(false);

        var plans = new List<ImportRowPlan>();
        var creates = new List<(string, ProductDetails)>();
        foreach (var row in rows)
        {
            var rowIssues = Columns.Where(c => c.Required && row[c.Key] is null).Select(c => ImportIssue.Error(row, c.Key, ImportCodes.ValueRequired)).ToList();
            var code = ProductRules.Code(row["code"]);
            if (row["code"] is not null && !code.IsSuccess)
            {
                rowIssues.Add(ImportIssue.Error(row, "code", code.Error!));
            }

            Guid? materialId = null;
            if (row["material"] is { } materialCode)
            {
                if (sold.TryGetValue(materialCode.Trim(), out var id))
                {
                    materialId = id;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "material", ProductOperations.MaterialNotSold));
                }
            }

            var product = code.IsSuccess ? existing.GetValueOrDefault(code.Value) : null;
            if (product is { Status: ProductStatus.Archived })
            {
                rowIssues.Add(ImportIssue.Error(row, "code", Archived));
            }

            ProductDetails? details = null;
            if (rowIssues.Count == 0)
            {
                var unit = row["saleUnit"]?.Trim().ToLowerInvariant() ?? (product is null ? null : CustomerRules.BasisCode(product.SaleUnit));
                var checkedDetails = ProductRules.Details(row["name"], materialId, unit);
                if (checkedDetails.IsSuccess)
                {
                    details = checkedDetails.Value;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, ColumnOfError.GetValueOrDefault(checkedDetails.Error!.Code, "code"), checkedDetails.Error!));
                }
            }

            issues.AddRange(rowIssues);
            if (rowIssues.Count > 0 || duplicated.Contains(row.Number) || details is null)
            {
                continue;
            }

            if (product is null)
            {
                creates.Add((code.Value, details));
                plans.Add(new ImportRowPlan(row.Number, code.Value, ImportAction.Create));
                continue;
            }

            var changed = product.Change(clock, details, caller.Subject);
            plans.Add(new ImportRowPlan(row.Number, code.Value, changed ? ImportAction.Update : ImportAction.Unchanged));
        }

        return (new ImportValidation(issues, plans), creates);
    }

    private static string? CodeOf(ImportRow row) => ProductRules.Code(row["code"]) is { IsSuccess: true } code ? code.Value : null;
}
