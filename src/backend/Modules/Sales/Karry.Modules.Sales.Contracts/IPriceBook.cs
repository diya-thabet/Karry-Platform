namespace Karry.Modules.Sales.Contracts;

/// <summary>
/// A unit price as other modules see it (the weighbridge freezes it on the ticket, ADR-0042 §2): per the product's
/// sale unit ("t" or "m3-loose"), from the price list version <c>PriceListId</c> in force since <c>EffectiveFrom</c>;
/// <c>Source</c> is "customer" (the customer's own list) or "site".
/// </summary>
public sealed record PriceQuote(
    Guid ProductId, decimal UnitPrice, string Currency, string SaleUnit, Guid PriceListId, DateOnly EffectiveFrom, string Source);

/// <summary>The company's prices, read in the caller's unit of work (RLS).</summary>
public interface IPriceBook
{
    /// <summary>
    /// The price of a product for a customer at a site at an instant (dates on the company's clock); null when no
    /// list prices it (the desk says so).
    /// </summary>
    Task<PriceQuote?> PriceAtAsync(Guid siteId, Guid? customerId, Guid productId, DateTimeOffset instant, CancellationToken cancellationToken);
}
