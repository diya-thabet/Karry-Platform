using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Sales.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Roles = Karry.SharedKernel.Authorization.DefaultRoles;

namespace Karry.Modules.Sales.Application;

/// <summary>
/// Sales permissions (ADR-0022, Phase 3 sub-phases §3.4). Customers are read by everyone who sells, weighs or invoices;
/// changing them, blocking them included, is the commercial side's.
/// </summary>
public static class SalesPermissions
{
    /// <summary>See customers (the weighbridge needs them to issue tickets).</summary>
    public const string CustomersRead = "sales.customers.read";

    /// <summary>Create and change customers, block and unblock them, set their credit limit (US-P3-SAL-01).</summary>
    public const string CustomersManage = "sales.customers.manage";

    /// <summary>A customer's credit limit: commercial data, masked for roles that do not need it.</summary>
    public const string CreditLimitField = "sales.customers.credit-limit";

    /// <summary>See products and price list versions (their prices are the sensitive field <see cref="UnitPriceField"/>).</summary>
    public const string ProductsRead = "sales.products.read";

    /// <summary>Create, change, archive and restore products (US-P3-SAL-02 AC1).</summary>
    public const string ProductsManage = "sales.products.manage";

    /// <summary>Create, change and withdraw price list versions (US-P3-SAL-02 AC2).</summary>
    public const string PricesManage = "sales.prices.manage";

    /// <summary>
    /// A unit price, wherever it appears (price lists, price previews): "sales.prices.read" of the plan is read access to
    /// this field; the weighmaster reads it (the ticket shows it), the site manager and the controller do not.
    /// </summary>
    public const string UnitPriceField = "sales.prices.unit-price";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(CustomersRead, ScopeKind.Tenant, privileged: false,
            Roles.TenantAdmin, Roles.Executive, Roles.Finance, Roles.Commercial, Roles.Controller, Roles.SiteManager, Roles.Weighmaster),
        new(CustomersManage, ScopeKind.Tenant, privileged: false, Roles.TenantAdmin, Roles.Commercial),
        new(ProductsRead, ScopeKind.Tenant, privileged: false,
            Roles.TenantAdmin, Roles.Executive, Roles.Finance, Roles.Commercial, Roles.Controller, Roles.SiteManager, Roles.Weighmaster),
        new(ProductsManage, ScopeKind.Tenant, privileged: false, Roles.TenantAdmin, Roles.Commercial),
        new(PricesManage, ScopeKind.Tenant, privileged: false, Roles.TenantAdmin, Roles.Commercial, Roles.Finance),
    ];

    public static IReadOnlyList<SensitiveFieldDefinition> SensitiveFields { get; } =
    [
        new(CreditLimitField, new Dictionary<string, FieldAccess>
        {
            [Roles.TenantAdmin] = FieldAccess.Write,
            [Roles.Commercial] = FieldAccess.Write,
            [Roles.Finance] = FieldAccess.Read,
            [Roles.Executive] = FieldAccess.Read,
        }),
        new(UnitPriceField, new Dictionary<string, FieldAccess>
        {
            [Roles.TenantAdmin] = FieldAccess.Write,
            [Roles.Commercial] = FieldAccess.Write,
            [Roles.Finance] = FieldAccess.Write,
            [Roles.Executive] = FieldAccess.Read,
            [Roles.Weighmaster] = FieldAccess.Read,
        }),
    ];
}

/// <summary>An amount in a currency, as the API receives it.</summary>
public sealed record MoneyInput(decimal? Amount, string? Currency);

/// <summary>
/// A customer as the API receives it; the code is given once, at creation. The credit limit is the sensitive field
/// <see cref="SalesPermissions.CreditLimitField"/>: a role that may not write it cannot send it.
/// </summary>
public sealed record CustomerInput(
    string? Code, string? LegalName, string? TaxId, IReadOnlyList<CustomerAddress>? Addresses, IReadOnlyList<CustomerContact>? Contacts,
    int? PaymentTermsDays, string? Basis, IReadOnlyList<Guid>? AllowedSiteIds,
    [property: Sensitive(SalesPermissions.CreditLimitField)] MoneyInput? CreditLimit);

public sealed record BlockInput(string? Reason);

/// <summary>Persistence port of customers (the caller's unit of work, RLS).</summary>
public interface ICustomerStore
{
    Task<Customer?> FindAsync(CustomerId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Customer>> ListAsync(CustomerStatus? status, string? search, CancellationToken cancellationToken);

    /// <summary>Saves with its events; false, with nothing saved, when the code is taken or the customer changed since it was loaded.</summary>
    Task<bool> TrySaveAsync(Customer customer, CancellationToken cancellationToken);

    /// <summary>The customers with these codes (imports), tracked so that changes to them are saved by <see cref="TrySaveAllAsync"/>.</summary>
    Task<IReadOnlyList<Customer>> FindByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Adds a new customer to the unit of work, saved by <see cref="TrySaveAllAsync"/>.</summary>
    void Add(Customer customer);

    /// <summary>Saves every pending change in one transaction; false, with nothing saved, on a code or version conflict.</summary>
    Task<bool> TrySaveAllAsync(CancellationToken cancellationToken);
}

/// <summary>US-P3-SAL-01: the company's customers, each change in the company's ledger.</summary>
public sealed class CustomerOperations(ICustomerStore store, ISiteDirectory sites, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("sales.customer_not_found", "No such customer in this company.");

    public static readonly Error CodeTaken = Error.Conflict("sales.customer_code_taken", "Another customer of the company already has this code.");

    public static readonly Error SiteUnknown = Error.Validation("sales.customer_site_unknown", "An allowed site is not a site of this company.");

    public async Task<IReadOnlyList<Customer>> ListAsync(string? status, string? search, CancellationToken cancellationToken)
    {
        CustomerStatus? wanted = status switch
        {
            "active" => CustomerStatus.Active,
            "blocked" => CustomerStatus.Blocked,
            _ => null,
        };
        return [.. (await store.ListAsync(wanted, search?.Trim(), cancellationToken).ConfigureAwait(false)).OrderBy(c => c.Code, StringComparer.Ordinal)];
    }

    public async Task<Result<Customer>> GetAsync(CustomerId id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } customer ? customer : NotFound;

    public async Task<Result<Customer>> CreateAsync(CustomerInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var code = CustomerRules.Code(input.Code);
        if (!code.IsSuccess)
        {
            return code.Error;
        }

        var details = await DetailsAsync(input, cancellationToken).ConfigureAwait(false);
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        var limit = CustomerRules.CreditLimit(input.CreditLimit?.Amount, input.CreditLimit?.Currency);
        if (!limit.IsSuccess)
        {
            return limit.Error;
        }

        var customer = Customer.Create(clock, Tenant, code.Value, details.Value, caller.Subject);
        customer.SetCreditLimit(clock, limit.Value, caller.Subject);
        return await store.TrySaveAsync(customer, cancellationToken).ConfigureAwait(false) ? customer : CodeTaken;
    }

    /// <summary>New details and credit limit; with <paramref name="expectedVersion"/> (<c>If-Match</c>) only the edited version changes (H-C1).</summary>
    public async Task<Result<Customer>> ChangeAsync(CustomerId id, CustomerInput input, CancellationToken cancellationToken, uint? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var customer = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!customer.IsSuccess)
        {
            return customer;
        }

        if (!RecordVersions.Matches(customer.Value.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        var details = await DetailsAsync(input, cancellationToken).ConfigureAwait(false);
        if (!details.IsSuccess)
        {
            return details.Error;
        }

        var changed = customer.Value.Change(clock, details.Value, caller.Subject);

        // No credit limit sent: unchanged (a role that cannot see it must not clear it by leaving it out).
        // `{"amount": null}` clears it.
        if (input.CreditLimit is { } sent)
        {
            var limit = CustomerRules.CreditLimit(sent.Amount, sent.Currency);
            if (!limit.IsSuccess)
            {
                return limit.Error;
            }

            changed |= customer.Value.SetCreditLimit(clock, limit.Value, caller.Subject);
        }

        if (changed && !await store.TrySaveAsync(customer.Value, cancellationToken).ConfigureAwait(false))
        {
            return RecordVersions.Conflict;
        }

        return customer;
    }

    public async Task<Result<Customer>> BlockAsync(CustomerId id, BlockInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The addressed customer first, then the body (ADR-0022): another company's customer is a 404, whatever was sent.
        var customer = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!customer.IsSuccess)
        {
            return customer;
        }

        var reason = CustomerRules.Reason(input.Reason);
        if (!reason.IsSuccess)
        {
            return reason.Error;
        }

        if (customer.Value.Block(clock, reason.Value, caller.Subject) && !await store.TrySaveAsync(customer.Value, cancellationToken).ConfigureAwait(false))
        {
            return RecordVersions.Conflict;
        }

        return customer;
    }

    public async Task<Result<Customer>> UnblockAsync(CustomerId id, CancellationToken cancellationToken)
    {
        var customer = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (customer.IsSuccess && customer.Value.Unblock(clock, caller.Subject) &&
            !await store.TrySaveAsync(customer.Value, cancellationToken).ConfigureAwait(false))
        {
            return RecordVersions.Conflict;
        }

        return customer;
    }

    private async Task<Result<CustomerDetails>> DetailsAsync(CustomerInput input, CancellationToken cancellationToken)
    {
        var details = CustomerRules.Details(input.LegalName, input.TaxId, input.Addresses, input.Contacts, input.PaymentTermsDays, input.Basis, input.AllowedSiteIds);
        if (!details.IsSuccess)
        {
            return details;
        }

        // Only sites of this company (the directory reads under RLS: another company's site is simply not found).
        foreach (var siteId in details.Value.AllowedSiteIds)
        {
            if (await sites.FindAsync(siteId, cancellationToken).ConfigureAwait(false) is null)
            {
                return SiteUnknown;
            }
        }

        return details;
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Sales needs a tenant.");
}
