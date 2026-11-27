using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Monetary;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Sales.Domain;

public readonly record struct PriceListId(Guid Value) : IEntityId<PriceListId>
{
    public static PriceListId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

public enum PriceListStatus
{
    Active = 1,

    /// <summary>Taken back before it came into force: never priced anything, kept for the record.</summary>
    Withdrawn = 2,
}

/// <summary>The price of one product per its sale unit, in the list's currency.</summary>
public sealed record PriceLine(Guid ProductId, decimal UnitPrice);

[EventType("sales.price_list_created.v1")]
[AuditSubject("price_list", nameof(PriceListId))]
public sealed record PriceListCreated(
    Guid PriceListId, Guid SiteId, Guid? CustomerId, string Currency, DateOnly EffectiveFrom, IReadOnlyList<PriceLine> Lines, string? Actor) : DomainEvent;

[EventType("sales.price_list_changed.v1")]
[AuditSubject("price_list", nameof(PriceListId))]
public sealed record PriceListChanged(Guid PriceListId, IReadOnlyList<PriceLine> Before, IReadOnlyList<PriceLine> After, string? Actor) : DomainEvent;

[EventType("sales.price_list_withdrawn.v1")]
[AuditSubject("price_list", nameof(PriceListId))]
public sealed record PriceListWithdrawn(Guid PriceListId, string? Actor) : DomainEvent;

/// <summary>
/// One version of a price list (US-P3-SAL-02 AC2, ADR-0042): the prices of a site, or of one customer at a site, from a
/// date on the company's clock until the next version. Once in force a version never changes (tickets were priced from
/// it): a correction is a new version. Before that it can be changed or withdrawn.
/// </summary>
public sealed class PriceList : AggregateRoot<PriceListId>, ITenantOwned
{
    private PriceList(
        PriceListId id, TenantId tenantId, Guid siteId, Guid? customerId, string currency, DateOnly effectiveFrom, IReadOnlyList<PriceLine> lines,
        DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        SiteId = siteId;
        CustomerId = customerId;
        Currency = currency;
        EffectiveFrom = effectiveFrom;
        Lines = lines;
        Status = PriceListStatus.Active;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private PriceList()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public Guid SiteId { get; private set; }

    /// <summary>The customer the prices are for; null = the site's prices for every customer.</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>ISO 4217 code of every price in the list.</summary>
    public string Currency { get; private set; } = null!;

    /// <summary>First day (company's clock) the prices apply; they apply until the next version's first day.</summary>
    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>One line per product, ordered by product.</summary>
    public IReadOnlyList<PriceLine> Lines { get; private set; } = [];

    public PriceListStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>The record version (PostgreSQL <c>xmin</c>), sent back as <c>If-Match</c> (H-C1).</summary>
    public uint Version { get; private set; }

    public static Result<PriceList> Create(
        TimeProvider clock, TenantId tenant, DateOnly today, Guid siteId, Guid? customerId, string currency, DateOnly effectiveFrom,
        IReadOnlyList<PriceLine> lines, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lines);
        if (effectiveFrom < today)
        {
            // Tickets already issued were priced from the lists then in force: prices are never back-dated.
            return PriceRules.BackDated;
        }

        var at = clock.GetUtcNow();
        var list = new PriceList(EntityId.New<PriceListId>(clock), tenant, siteId, customerId, currency, effectiveFrom, lines, at);
        list.Raise(new PriceListCreated(list.Id.Value, siteId, customerId, currency, effectiveFrom, lines, actor) { OccurredAt = at });
        return list;
    }

    /// <summary>In force on <paramref name="date"/> (company's clock), or after; a version in force never changes.</summary>
    public bool InForceBy(DateOnly date) => EffectiveFrom <= date;

    /// <summary>New lines while not yet in force; false when unchanged.</summary>
    public Result<bool> ChangeLines(TimeProvider clock, DateOnly today, IReadOnlyList<PriceLine> lines, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lines);
        if (Status == PriceListStatus.Withdrawn)
        {
            return PriceRules.Withdrawn;
        }

        if (InForceBy(today))
        {
            return PriceRules.InForce;
        }

        if (Lines.SequenceEqual(lines))
        {
            return false;
        }

        var before = Lines;
        Lines = lines;
        ChangedAt = clock.GetUtcNow();
        Raise(new PriceListChanged(Id.Value, before, lines, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Takes back a version not yet in force; false when already withdrawn.</summary>
    public Result<bool> Withdraw(TimeProvider clock, DateOnly today, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == PriceListStatus.Withdrawn)
        {
            return false;
        }

        if (InForceBy(today))
        {
            return PriceRules.InForce;
        }

        Status = PriceListStatus.Withdrawn;
        ChangedAt = clock.GetUtcNow();
        Raise(new PriceListWithdrawn(Id.Value, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>The line of <paramref name="productId"/>, or null.</summary>
    public PriceLine? LineOf(Guid productId) => Lines.FirstOrDefault(l => l.ProductId == productId);
}

/// <summary>A resolved price: the unit price and the list version it came from.</summary>
public sealed record ResolvedPrice(PriceList List, PriceLine Line)
{
    public Money UnitPrice => Money.Of(Line.UnitPrice, List.Currency);

    /// <summary>"customer" when the customer's own list gave the price, "site" otherwise.</summary>
    public string Source => List.CustomerId is null ? "site" : "customer";
}

/// <summary>Price list input rules and price resolution (US-P3-SAL-02, ADR-0042 §2).</summary>
public static class PriceRules
{
    public const int MaxLines = 500;
    public const int MaxDecimals = 4;

    public static readonly Error BackDated = Error.Validation(
        "sales.price_list_back_dated", "A price list starts today or later (company's clock): prices are never back-dated.");

    public static readonly Error InForce = Error.Conflict(
        "sales.price_list_in_force", "This version is in force: it cannot change. Create a new version from a later date.");

    public static readonly Error Withdrawn = Error.Conflict("sales.price_list_withdrawn", "This version was withdrawn.");

    public static readonly Error CurrencyInvalid = Error.Validation("sales.price_list_currency_invalid", "The currency is a known ISO 4217 code (e.g. TND, EUR).");

    public static readonly Error LinesInvalid = Error.Validation(
        "sales.price_list_lines_invalid",
        $"A price list has 1 to {MaxLines} lines, one per product, each a positive unit price with at most {MaxDecimals} decimals.");

    /// <summary>The ISO code, upper case, when it is a currency Karry knows.</summary>
    public static Result<string> Currency(string? currency)
    {
        var code = currency?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
        {
            return CurrencyInvalid;
        }

        try
        {
            return SharedKernel.Monetary.Currency.Of(code).Code;
        }
        catch (ArgumentException)
        {
            return CurrencyInvalid;
        }
    }

    /// <summary>Checked lines, ordered by product so that equal lists compare equal.</summary>
    public static Result<IReadOnlyList<PriceLine>> Lines(IReadOnlyList<PriceLine?>? lines)
    {
        if (lines is not { Count: > 0 and <= MaxLines } || lines.Any(l => l is null || l.ProductId == Guid.Empty || l.UnitPrice <= 0m || l.UnitPrice.Scale > MaxDecimals))
        {
            return LinesInvalid;
        }

        var tidy = lines.Select(l => l!).OrderBy(l => l.ProductId).ToList();
        return tidy.Select(l => l.ProductId).Distinct().Count() == tidy.Count ? tidy : LinesInvalid;
    }

    /// <summary>
    /// The price of <paramref name="productId"/> for <paramref name="customerId"/> at a site on <paramref name="date"/>
    /// (company's clock), from that site's versions: the customer's version in force wins over the site's version in
    /// force; a version in force is the active one with the latest start on or before the date. When the customer's
    /// version has no line for the product, the site's price applies. Null: no price (the desk says so).
    /// </summary>
    public static ResolvedPrice? Resolve(IEnumerable<PriceList> siteVersions, Guid? customerId, Guid productId, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(siteVersions);
        var inForce = siteVersions.Where(v => v.Status == PriceListStatus.Active && v.InForceBy(date)).ToList();

        static PriceList? Latest(IEnumerable<PriceList> versions) => versions.OrderByDescending(v => v.EffectiveFrom).FirstOrDefault();

        if (customerId is { } customer && Latest(inForce.Where(v => v.CustomerId == customer)) is { } own && own.LineOf(productId) is { } ownLine)
        {
            return new ResolvedPrice(own, ownLine);
        }

        return Latest(inForce.Where(v => v.CustomerId is null)) is { } site && site.LineOf(productId) is { } siteLine
            ? new ResolvedPrice(site, siteLine)
            : null;
    }
}
