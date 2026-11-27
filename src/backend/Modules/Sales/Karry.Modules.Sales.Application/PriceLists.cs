using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Sales.Contracts;
using Karry.Modules.Sales.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using NodaTime;

namespace Karry.Modules.Sales.Application;

/// <summary>One line as the API receives it: the unit price is the sensitive field <see cref="SalesPermissions.UnitPriceField"/>.</summary>
public sealed record PriceLineInput(Guid? ProductId, [property: Sensitive(SalesPermissions.UnitPriceField)] decimal? UnitPrice);

/// <summary>A new price list version; <c>CustomerId</c> null = the site's prices for every customer.</summary>
public sealed record PriceListInput(Guid? SiteId, Guid? CustomerId, string? Currency, DateOnly? EffectiveFrom, IReadOnlyList<PriceLineInput>? Lines);

/// <summary>New lines for a version not yet in force.</summary>
public sealed record PriceLinesInput(IReadOnlyList<PriceLineInput>? Lines);

/// <summary>Persistence port of price lists (the caller's unit of work, RLS).</summary>
public interface IPriceListStore
{
    Task<PriceList?> FindAsync(PriceListId id, CancellationToken cancellationToken);

    /// <summary>Versions, newest start first, of a site (or every site), optionally only the site's own or one customer's.</summary>
    Task<IReadOnlyList<PriceList>> ListAsync(Guid? siteId, Guid? customerId, bool siteOnly, CancellationToken cancellationToken);

    /// <summary>The active versions of a site for the site and for one customer, the only ones a resolution reads.</summary>
    Task<IReadOnlyList<PriceList>> ActiveForAsync(Guid siteId, Guid? customerId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves with its events; false, with nothing saved, when an active version of the same site and customer already
    /// starts that day or the version changed since it was loaded.
    /// </summary>
    Task<bool> TrySaveAsync(PriceList list, CancellationToken cancellationToken);
}

/// <summary>US-P3-SAL-02 AC2: price list versions per site and per customer, and the price at a moment (ADR-0042 §2).</summary>
public sealed class PriceListOperations(
    IPriceListStore store, IProductStore products, ICustomerStore customers, ISiteDirectory sites, ITenantDirectory tenants, ICallerAccess caller,
    ITenantContext tenant, TimeProvider clock) : IPriceBook
{
    public static readonly Error NotFound = Error.NotFound("sales.price_list_not_found", "No such price list in this company.");

    public static readonly Error NoPrice = Error.NotFound("sales.no_price", "No price list in force prices this product for this customer at this site.");

    public static readonly Error SameStart = Error.Conflict(
        "sales.price_list_same_start", "A version of this list already starts that day: change it, or withdraw it first.");

    public static readonly Error SiteUnknown = Error.Validation("sales.price_list_site_unknown", "The site is not a site of this company.");

    public static readonly Error CustomerUnknown = Error.Validation("sales.price_list_customer_unknown", "The customer is not a customer of this company.");

    public static readonly Error ProductUnknown = Error.Validation(
        "sales.price_list_product_unknown", "A line names a product that is not an active product of this company.");

    public static readonly Error EffectiveFromRequired = Error.Validation("sales.price_list_effective_from_required", "Give the first day the prices apply.");

    public async Task<IReadOnlyList<PriceList>> ListAsync(Guid? siteId, Guid? customerId, bool siteOnly, CancellationToken cancellationToken) =>
        await store.ListAsync(siteId, customerId, siteOnly, cancellationToken).ConfigureAwait(false);

    public async Task<Result<PriceList>> GetAsync(PriceListId id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } list ? list : NotFound;

    public async Task<Result<PriceList>> CreateAsync(PriceListInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SiteId is not { } siteId || await sites.FindAsync(siteId, cancellationToken).ConfigureAwait(false) is not { Archived: false })
        {
            return SiteUnknown;
        }

        if (input.CustomerId is { } customerId && await customers.FindAsync(new CustomerId(customerId), cancellationToken).ConfigureAwait(false) is null)
        {
            return CustomerUnknown;
        }

        var currency = PriceRules.Currency(input.Currency);
        if (!currency.IsSuccess)
        {
            return currency.Error;
        }

        if (input.EffectiveFrom is not { } effectiveFrom)
        {
            return EffectiveFromRequired;
        }

        var lines = await LinesAsync(input.Lines, cancellationToken).ConfigureAwait(false);
        if (!lines.IsSuccess)
        {
            return lines.Error;
        }

        var today = await TodayAsync(cancellationToken).ConfigureAwait(false);
        var list = PriceList.Create(clock, Tenant, today, siteId, input.CustomerId, currency.Value, effectiveFrom, lines.Value, caller.Subject);
        if (!list.IsSuccess)
        {
            return list;
        }

        return await store.TrySaveAsync(list.Value, cancellationToken).ConfigureAwait(false) ? list : SameStart;
    }

    /// <summary>New lines while the version is not in force; <paramref name="expectedVersion"/> is <c>If-Match</c> (H-C1).</summary>
    public async Task<Result<PriceList>> ChangeLinesAsync(PriceListId id, PriceLinesInput input, CancellationToken cancellationToken, uint? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var list = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!list.IsSuccess)
        {
            return list;
        }

        if (!RecordVersions.Matches(list.Value.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        var lines = await LinesAsync(input.Lines, cancellationToken).ConfigureAwait(false);
        if (!lines.IsSuccess)
        {
            return lines.Error;
        }

        var changed = list.Value.ChangeLines(clock, await TodayAsync(cancellationToken).ConfigureAwait(false), lines.Value, caller.Subject);
        if (!changed.IsSuccess)
        {
            return changed.Error;
        }

        return changed.Value && !await store.TrySaveAsync(list.Value, cancellationToken).ConfigureAwait(false) ? RecordVersions.Conflict : list;
    }

    public async Task<Result<PriceList>> WithdrawAsync(PriceListId id, CancellationToken cancellationToken)
    {
        var list = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (!list.IsSuccess)
        {
            return list;
        }

        var withdrawn = list.Value.Withdraw(clock, await TodayAsync(cancellationToken).ConfigureAwait(false), caller.Subject);
        if (!withdrawn.IsSuccess)
        {
            return withdrawn.Error;
        }

        return withdrawn.Value && !await store.TrySaveAsync(list.Value, cancellationToken).ConfigureAwait(false) ? RecordVersions.Conflict : list;
    }

    /// <summary>The price of a product (the portal's "price on date" preview and the weighbridge, ADR-0042 §2).</summary>
    public async Task<Result<PriceQuote>> QuoteAsync(Guid siteId, Guid? customerId, Guid productId, DateTimeOffset instant, CancellationToken cancellationToken) =>
        await PriceAtAsync(siteId, customerId, productId, instant, cancellationToken).ConfigureAwait(false) is { } quote ? quote : NoPrice;

    public async Task<PriceQuote?> PriceAtAsync(Guid siteId, Guid? customerId, Guid productId, DateTimeOffset instant, CancellationToken cancellationToken)
    {
        var product = await products.FindAsync(new ProductId(productId), cancellationToken).ConfigureAwait(false);
        if (product is null)
        {
            return null;
        }

        var date = await DateOnClockAsync(instant, cancellationToken).ConfigureAwait(false);
        var versions = await store.ActiveForAsync(siteId, customerId, cancellationToken).ConfigureAwait(false);
        return PriceRules.Resolve(versions, customerId, productId, date) is { } price
            ? new PriceQuote(productId, price.Line.UnitPrice, price.List.Currency, CustomerRules.BasisCode(product.SaleUnit), price.List.Id.Value,
                price.List.EffectiveFrom, price.Source)
            : null;
    }

    /// <summary>Today on the company's clock (its settings' time zone).</summary>
    public Task<DateOnly> TodayAsync(CancellationToken cancellationToken) => DateOnClockAsync(clock.GetUtcNow(), cancellationToken);

    private async Task<DateOnly> DateOnClockAsync(DateTimeOffset instant, CancellationToken cancellationToken)
    {
        var zone = (await tenants.FindAsync(Tenant, cancellationToken).ConfigureAwait(false))?.TimeZone ?? "UTC";
        var date = Instant.FromDateTimeOffset(instant).InZone(DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone) ?? DateTimeZone.Utc).Date;
        return new DateOnly(date.Year, date.Month, date.Day);
    }

    /// <summary>Checked lines, each naming an active product of this company.</summary>
    private async Task<Result<IReadOnlyList<PriceLine>>> LinesAsync(IReadOnlyList<PriceLineInput>? input, CancellationToken cancellationToken)
    {
        var lines = PriceRules.Lines(input?.Select(l => l is { ProductId: { } product, UnitPrice: { } price } ? new PriceLine(product, price) : null).ToList());
        if (!lines.IsSuccess)
        {
            return lines;
        }

        foreach (var line in lines.Value)
        {
            if (await products.FindAsync(new ProductId(line.ProductId), cancellationToken).ConfigureAwait(false) is not { Status: ProductStatus.Active })
            {
                return ProductUnknown;
            }
        }

        return lines;
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Sales needs a tenant.");
}
