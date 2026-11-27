using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Sales.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Sales.Application;

/// <summary>
/// The <c>customers</c> import (US-P3-SAL-01, ADR-0027, TC-P3-SAL-004): one row per customer, keyed by its code. A new
/// code creates the customer; a known code changes its legal name, tax ID, payment terms and basis, and its billing
/// address, contact and allowed sites only when those cells are filled (empty keeps what the customer has). Sites are
/// codes separated by commas or semicolons. The credit limit is not imported (a sensitive field, set on screen). Blocked
/// customers are changed like any other: blocking is a decision made on screen. Nothing is saved unless every row is valid.
/// </summary>
public sealed class CustomerImport(ICustomerStore store, ISiteDirectory sites, ICallerAccess caller, ITenantContext tenant, TimeProvider clock) : IImportTarget
{
    private static readonly char[] SiteSeparators = [',', ';'];

    private static readonly Dictionary<string, string> ColumnOfError = new(StringComparer.Ordinal)
    {
        [CustomerRules.CodeInvalid.Code] = "code",
        [CustomerRules.NameInvalid.Code] = "legalName",
        [CustomerRules.TaxIdInvalid.Code] = "taxId",
        [CustomerRules.PaymentTermsInvalid.Code] = "paymentTermsDays",
        [CustomerRules.BasisInvalid.Code] = "basis",
        [CustomerRules.AddressInvalid.Code] = "address",
        [CustomerRules.ContactInvalid.Code] = "contactName",
    };

    public string Kind => "customers";

    public string Permission => SalesPermissions.CustomersManage;

    public IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new("code", "Code", "Code", Required: true, Example: "CLI-0042"),
        new("legalName", "Legal name", "Raison sociale", Required: true, Example: "Carrières du Nord SARL"),
        new("taxId", "Tax ID", "Matricule fiscal", Required: false, Example: "1234567A"),
        new("paymentTermsDays", "Payment terms (days)", "Délai de paiement (jours)", Required: false, ImportColumnType.Number, Example: "30"),
        new("basis", "Invoicing basis", "Base de facturation", Required: false, List: "bases", Example: "t"),
        new("address", "Billing address", "Adresse de facturation", Required: false, Example: "12 rue du Port"),
        new("city", "City", "Ville", Required: false, Example: "Bizerte"),
        new("country", "Country", "Pays", Required: false, Example: "TN"),
        new("contactName", "Contact name", "Nom du contact", Required: false, Example: "Awa Diallo"),
        new("contactPhone", "Contact phone", "Téléphone du contact", Required: false, Example: "+216 71 000 000"),
        new("contactEmail", "Contact e-mail", "E-mail du contact", Required: false, Example: "achats@nord.example"),
        new("sites", "Allowed sites (codes)", "Sites autorisés (codes)", Required: false, List: "sites", Example: "NORD"),
    ];

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken)
    {
        var siteCodes = (await sites.ListAsync(cancellationToken).ConfigureAwait(false)).Where(s => !s.Archived).Select(s => s.Code).ToList();
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["bases"] = [CustomerRules.BasisCode(InvoicingBasis.Tonnes), CustomerRules.BasisCode(InvoicingBasis.CubicMetresLoose)],
            ["sites"] = siteCodes,
        };
    }

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
            store.Add(Customer.Create(clock, Tenant, code, details, caller.Subject));
        }

        return await store.TrySaveAllAsync(cancellationToken).ConfigureAwait(false)
            ? ImportCommits.Of(prepared.Validation)
            : ImportCommits.DataChanged(prepared.Validation);
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Sales needs a tenant.");

    private async Task<(ImportValidation Validation, List<(string Code, CustomerDetails Details)> Creates)> PrepareAsync(
        IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var issues = ImportCodes.Duplicates(rows, "code", CodeOf).ToList();
        var duplicated = issues.Select(i => i.Row).ToHashSet();
        var codes = rows.Select(CodeOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var existing = (await store.FindByCodesAsync(codes, cancellationToken).ConfigureAwait(false)).ToDictionary(c => c.Code, StringComparer.Ordinal);
        var siteByCode = (await sites.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(s => !s.Archived).ToDictionary(s => s.Code, s => s.Id, StringComparer.OrdinalIgnoreCase);

        var plans = new List<ImportRowPlan>();
        var creates = new List<(string, CustomerDetails)>();
        foreach (var row in rows)
        {
            var rowIssues = Columns.Where(c => c.Required && row[c.Key] is null).Select(c => ImportIssue.Error(row, c.Key, ImportCodes.ValueRequired)).ToList();
            var code = CustomerRules.Code(row["code"]);
            if (row["code"] is not null && !code.IsSuccess)
            {
                rowIssues.Add(ImportIssue.Error(row, "code", code.Error!));
            }

            int? terms = null;
            if (row["paymentTermsDays"] is { } termsText)
            {
                if (ImportValues.TryInteger(termsText, out var parsed))
                {
                    terms = parsed;
                }
                else
                {
                    rowIssues.Add(ImportIssue.Error(row, "paymentTermsDays", ImportCodes.NumberInvalid));
                }
            }

            List<Guid>? siteIds = null;
            if (row["sites"] is { } siteText)
            {
                siteIds = [];
                foreach (var siteCode in siteText.Split(SiteSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (siteByCode.TryGetValue(siteCode, out var siteId))
                    {
                        siteIds.Add(siteId);
                    }
                    else
                    {
                        rowIssues.Add(ImportIssue.Error(row, "sites", CustomerOperations.SiteUnknown));
                        break;
                    }
                }
            }

            var customer = code.IsSuccess ? existing.GetValueOrDefault(code.Value) : null;
            CustomerDetails? details = null;
            if (rowIssues.Count == 0)
            {
                var addressGiven = row["address"] is not null || row["city"] is not null || row["country"] is not null;
                var contactGiven = row["contactName"] is not null || row["contactPhone"] is not null || row["contactEmail"] is not null;
                IReadOnlyList<CustomerAddress> addresses = addressGiven
                    ? [new CustomerAddress("billing", row["address"]!, null, row["city"]!, null, null, row["country"]!)]
                    : customer?.Addresses ?? [];
                IReadOnlyList<CustomerContact> contacts = contactGiven
                    ? [new CustomerContact(row["contactName"]!, null, row["contactPhone"], row["contactEmail"])]
                    : customer?.Contacts ?? [];
                var basis = row["basis"]?.Trim().ToLowerInvariant() ?? (customer is null ? null : CustomerRules.BasisCode(customer.Basis));
                var checkedDetails = CustomerRules.Details(row["legalName"], row["taxId"], addresses, contacts, terms ?? customer?.PaymentTermsDays, basis,
                    siteIds ?? customer?.AllowedSiteIds);
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

            if (customer is null)
            {
                creates.Add((code.Value, details));
                plans.Add(new ImportRowPlan(row.Number, code.Value, ImportAction.Create));
                continue;
            }

            var changed = customer.Change(clock, details, caller.Subject);
            plans.Add(new ImportRowPlan(row.Number, code.Value, changed ? ImportAction.Update : ImportAction.Unchanged));
        }

        return (new ImportValidation(issues, plans), creates);
    }

    private static string? CodeOf(ImportRow row) => CustomerRules.Code(row["code"]) is { IsSuccess: true } code ? code.Value : null;
}
