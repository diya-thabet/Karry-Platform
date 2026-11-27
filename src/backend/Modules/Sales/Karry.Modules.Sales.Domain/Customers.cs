using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Monetary;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Sales.Domain;

public readonly record struct CustomerId(Guid Value) : IEntityId<CustomerId>
{
    public static CustomerId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>How a customer buys (US-P3-SAL-01 AC1): by mass, or by loose volume derived from the ticket's mass (spec review M2).</summary>
public enum InvoicingBasis
{
    Tonnes = 1,
    CubicMetresLoose = 2,
}

public enum CustomerStatus
{
    Active = 1,

    /// <summary>Gets no new ticket (US-P3-SAL-01 AC2) unless the site manager overrides it (RULE-WB-07).</summary>
    Blocked = 2,
}

/// <summary>A postal address; <c>Kind</c> is billing or delivery.</summary>
public sealed record CustomerAddress(string Kind, string Line1, string? Line2, string City, string? Region, string? PostalCode, string Country);

/// <summary>A person to call or write to at the customer.</summary>
public sealed record CustomerContact(string Name, string? Role, string? Phone, string? Email);

/// <summary>Everything a customer is, as entered and as stored in the ledger (no credit limit: it is sensitive, recorded apart).</summary>
public sealed record CustomerDetails(
    string LegalName, string? TaxId, IReadOnlyList<CustomerAddress> Addresses, IReadOnlyList<CustomerContact> Contacts,
    int PaymentTermsDays, InvoicingBasis Basis, IReadOnlyList<Guid> AllowedSiteIds);

[EventType("sales.customer_created.v1")]
[AuditSubject("customer", nameof(CustomerId))]
public sealed record CustomerCreated(Guid CustomerId, string Code, CustomerDetails Customer, string? Actor) : DomainEvent;

[EventType("sales.customer_changed.v1")]
[AuditSubject("customer", nameof(CustomerId))]
public sealed record CustomerChanged(Guid CustomerId, string Code, CustomerDetails Before, CustomerDetails After, string? Actor) : DomainEvent;

[EventType("sales.customer_credit_limit_set.v1")]
[AuditSubject("customer", nameof(CustomerId))]
public sealed record CustomerCreditLimitSet(Guid CustomerId, string Code, string? Before, string? After, string? Actor) : DomainEvent;

[EventType("sales.customer_blocked.v1")]
[AuditSubject("customer", nameof(CustomerId))]
public sealed record CustomerBlocked(Guid CustomerId, string Code, string Reason, string? Actor) : DomainEvent;

[EventType("sales.customer_unblocked.v1")]
[AuditSubject("customer", nameof(CustomerId))]
public sealed record CustomerUnblocked(Guid CustomerId, string Code, string? Actor) : DomainEvent;

/// <summary>
/// A customer of the company (US-P3-SAL-01, ADR-0042): who buys, how they are invoiced, where they may load, and whether
/// they may get tickets at all. Tenant-owned (RLS); never deleted (tickets refer to it for ever).
/// </summary>
public sealed class Customer : AggregateRoot<CustomerId>, ITenantOwned
{
    private Customer(CustomerId id, TenantId tenantId, string code, CustomerDetails details, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        Code = code;
        Apply(details);
        Status = CustomerStatus.Active;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private Customer()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    /// <summary>Short code the weighmaster types (e.g. "CLI-0042"), unique in the company for ever.</summary>
    public string Code { get; private set; } = null!;

    public string LegalName { get; private set; } = null!;

    public string? TaxId { get; private set; }

    public IReadOnlyList<CustomerAddress> Addresses { get; private set; } = [];

    public IReadOnlyList<CustomerContact> Contacts { get; private set; } = [];

    public int PaymentTermsDays { get; private set; }

    public InvoicingBasis Basis { get; private set; }

    /// <summary>The sites where the customer may load; empty = every site of the company.</summary>
    public IReadOnlyList<Guid> AllowedSiteIds { get; private set; } = [];

    /// <summary>Optional; a sensitive field (masked for roles without access).</summary>
    public Money? CreditLimit { get; private set; }

    public CustomerStatus Status { get; private set; }

    public string? BlockedReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>The record version (PostgreSQL <c>xmin</c>), sent back as <c>If-Match</c> (H-C1).</summary>
    public uint Version { get; private set; }

    public CustomerDetails Details => new(LegalName, TaxId, Addresses, Contacts, PaymentTermsDays, Basis, AllowedSiteIds);

    public static Customer Create(TimeProvider clock, TenantId tenant, string code, CustomerDetails details, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        var at = clock.GetUtcNow();
        var customer = new Customer(EntityId.New<CustomerId>(clock), tenant, code, details, at);
        customer.Raise(new CustomerCreated(customer.Id.Value, code, details, actor) { OccurredAt = at });
        return customer;
    }

    /// <summary>May the customer load at <paramref name="siteId"/>? (Allowed sites; empty means every site.)</summary>
    public bool MayLoadAt(Guid siteId) => AllowedSiteIds.Count == 0 || AllowedSiteIds.Contains(siteId);

    /// <summary>New details; false when nothing changed (no ledger entry). The code never changes.</summary>
    public bool Change(TimeProvider clock, CustomerDetails details, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(details);
        var before = Details;
        if (CustomerRules.Same(before, details))
        {
            return false;
        }

        Apply(details);
        ChangedAt = clock.GetUtcNow();
        Raise(new CustomerChanged(Id.Value, Code, before, details, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Sets (or clears, with null) the credit limit; false when unchanged.</summary>
    public bool SetCreditLimit(TimeProvider clock, Money? limit, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (limit == CreditLimit)
        {
            return false;
        }

        var before = CreditLimit;
        CreditLimit = limit;
        ChangedAt = clock.GetUtcNow();
        Raise(new CustomerCreditLimitSet(Id.Value, Code, CustomerRules.Text(before), CustomerRules.Text(limit), actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Blocks new tickets with a reason the weighmaster sees (AC2); a new reason on a blocked customer replaces it.</summary>
    public bool Block(TimeProvider clock, string reason, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == CustomerStatus.Blocked && string.Equals(BlockedReason, reason, StringComparison.Ordinal))
        {
            return false;
        }

        Status = CustomerStatus.Blocked;
        BlockedReason = reason;
        ChangedAt = clock.GetUtcNow();
        Raise(new CustomerBlocked(Id.Value, Code, reason, actor) { OccurredAt = ChangedAt });
        return true;
    }

    public bool Unblock(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == CustomerStatus.Active)
        {
            return false;
        }

        Status = CustomerStatus.Active;
        BlockedReason = null;
        ChangedAt = clock.GetUtcNow();
        Raise(new CustomerUnblocked(Id.Value, Code, actor) { OccurredAt = ChangedAt });
        return true;
    }

    private void Apply(CustomerDetails details)
    {
        LegalName = details.LegalName;
        TaxId = details.TaxId;
        Addresses = details.Addresses;
        Contacts = details.Contacts;
        PaymentTermsDays = details.PaymentTermsDays;
        Basis = details.Basis;
        AllowedSiteIds = details.AllowedSiteIds;
    }
}

/// <summary>Customer input rules (US-P3-SAL-01).</summary>
public static partial class CustomerRules
{
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 200;
    public const int TaxIdMaxLength = 40;
    public const int ReasonMaxLength = 300;
    public const int TextMaxLength = 200;
    public const int MaxPaymentTermsDays = 365;
    public const int MaxAddresses = 10;
    public const int MaxContacts = 20;

    public static readonly Error CodeInvalid = Error.Validation(
        "sales.customer_code_invalid", "A code is 1 to 20 capital letters, digits or dashes, starting with a letter or digit.");

    public static readonly Error NameInvalid = Error.Validation("sales.customer_name_invalid", $"The legal name is 1 to {NameMaxLength} characters.");

    public static readonly Error TaxIdInvalid = Error.Validation("sales.customer_tax_id_invalid", $"A tax ID is up to {TaxIdMaxLength} characters.");

    public static readonly Error PaymentTermsInvalid = Error.Validation(
        "sales.customer_payment_terms_invalid", $"Payment terms are 0 to {MaxPaymentTermsDays} days.");

    public static readonly Error BasisInvalid = Error.Validation("sales.customer_basis_invalid", "The invoicing basis is t or m3-loose.");

    public static readonly Error AddressInvalid = Error.Validation(
        "sales.customer_address_invalid", $"An address has a kind (billing or delivery), a first line, a city and a country; up to {MaxAddresses} addresses.");

    public static readonly Error ContactInvalid = Error.Validation(
        "sales.customer_contact_invalid", $"A contact has a name, and a valid e-mail when one is given; up to {MaxContacts} contacts.");

    public static readonly Error ReasonInvalid = Error.Validation(
        "sales.customer_block_reason_invalid", $"Blocking needs a reason the weighmaster can read (1 to {ReasonMaxLength} characters).");

    public static readonly Error CreditLimitInvalid = Error.Validation(
        "sales.customer_credit_limit_invalid", "A credit limit is a positive amount with a known currency.");

    public static Result<string> Code(string? code)
    {
        var value = code?.Trim().ToUpperInvariant();
        return value is not null && CodePattern().IsMatch(value) ? value : CodeInvalid;
    }

    public static Result<string> Reason(string? reason)
    {
        var value = reason?.Trim();
        return value is { Length: > 0 and <= ReasonMaxLength } ? value : ReasonInvalid;
    }

    public static Result<InvoicingBasis> Basis(string? basis) => basis switch
    {
        "t" => InvoicingBasis.Tonnes,
        "m3-loose" => InvoicingBasis.CubicMetresLoose,
        _ => BasisInvalid,
    };

    public static string BasisCode(InvoicingBasis basis) => basis switch
    {
        InvoicingBasis.Tonnes => "t",
        InvoicingBasis.CubicMetresLoose => "m3-loose",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, null),
    };

    /// <summary>Checks and tidies the details (trimmed text, normalised tax ID, sorted distinct sites).</summary>
    public static Result<CustomerDetails> Details(
        string? legalName, string? taxId, IReadOnlyList<CustomerAddress>? addresses, IReadOnlyList<CustomerContact>? contacts,
        int? paymentTermsDays, string? basis, IReadOnlyList<Guid>? allowedSiteIds)
    {
        var name = legalName?.Trim();
        if (name is not { Length: > 0 and <= NameMaxLength })
        {
            return NameInvalid;
        }

        var tax = string.IsNullOrWhiteSpace(taxId) ? null : TaxIdSpaces().Replace(taxId.Trim().ToUpperInvariant(), string.Empty);
        if (tax is { Length: > TaxIdMaxLength })
        {
            return TaxIdInvalid;
        }

        var terms = paymentTermsDays ?? 30;
        if (terms is < 0 or > MaxPaymentTermsDays)
        {
            return PaymentTermsInvalid;
        }

        var parsedBasis = Basis(basis ?? "t");
        if (!parsedBasis.IsSuccess)
        {
            return parsedBasis.Error;
        }

        var tidyAddresses = new List<CustomerAddress>();
        foreach (var address in addresses ?? [])
        {
            if (address is null || address.Kind is not ("billing" or "delivery") || Blank(address.Line1) || Blank(address.City) || Blank(address.Country) ||
                TooLong(address.Line1, address.Line2, address.City, address.Region, address.PostalCode, address.Country))
            {
                return AddressInvalid;
            }

            tidyAddresses.Add(new CustomerAddress(address.Kind, address.Line1.Trim(), Optional(address.Line2), address.City.Trim(), Optional(address.Region),
                Optional(address.PostalCode), address.Country.Trim().ToUpperInvariant()));
        }

        var tidyContacts = new List<CustomerContact>();
        foreach (var contact in contacts ?? [])
        {
            if (contact is null || Blank(contact.Name) || TooLong(contact.Name, contact.Role, contact.Phone, contact.Email) ||
                (Optional(contact.Email) is { } email && !MailAddress.TryCreate(email, out _)))
            {
                return ContactInvalid;
            }

            tidyContacts.Add(new CustomerContact(contact.Name.Trim(), Optional(contact.Role), Optional(contact.Phone), Optional(contact.Email)));
        }

        if (tidyAddresses.Count > MaxAddresses || tidyContacts.Count > MaxContacts)
        {
            return tidyAddresses.Count > MaxAddresses ? AddressInvalid : ContactInvalid;
        }

        var sites = (allowedSiteIds ?? []).Distinct().Order().ToList();
        return new CustomerDetails(name, tax, tidyAddresses, tidyContacts, terms, parsedBasis.Value, sites);
    }

    public static Result<Money?> CreditLimit(decimal? amount, string? currency)
    {
        if (amount is null)
        {
            return (Money?)null;
        }

        if (amount <= 0m || string.IsNullOrWhiteSpace(currency))
        {
            return CreditLimitInvalid;
        }

        try
        {
            return (Money?)Money.Of(amount.Value, currency.Trim().ToUpperInvariant());
        }
        catch (ArgumentException)
        {
            return CreditLimitInvalid;
        }
    }

    /// <summary>Equal details, list contents compared item by item (records compare lists by reference).</summary>
    public static bool Same(CustomerDetails a, CustomerDetails b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.LegalName == b.LegalName && a.TaxId == b.TaxId && a.PaymentTermsDays == b.PaymentTermsDays && a.Basis == b.Basis &&
               a.Addresses.SequenceEqual(b.Addresses) && a.Contacts.SequenceEqual(b.Contacts) && a.AllowedSiteIds.SequenceEqual(b.AllowedSiteIds);
    }

    public static string? Text(Money? money) =>
        money is { } value ? string.Create(CultureInfo.InvariantCulture, $"{value.Amount} {value.Currency.Code}") : null;

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool TooLong(params string?[] texts) => texts.Any(t => t is { Length: > TextMaxLength });

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex TaxIdSpaces();
}
