using Karry.SharedKernel.Monetary;

namespace Karry.Modules.Billing.Domain;

/// <summary>
/// Monthly recurring revenue of one subscription for the operator console (ADR-0029 §3): the plan version's price for
/// the subscription's period, per site and per extra asset, an annual price spread over 12 months, rounded to the
/// currency's minor units. Zero while nothing is billed (trial, suspended, cancelled, expired, deleted); a past-due or
/// restricted company is still billed. Add-ons have no price yet (N-01) and count for nothing. Illustrative until the
/// owner sets the prices.
/// </summary>
public static class MonthlyRevenue
{
    /// <summary>
    /// In <paramref name="currency"/> when the plan prices it, else in the plan's first currency for the period; null
    /// when a billed subscription's plan has no price for its period (an enterprise plan quoted by sales).
    /// </summary>
    public static Money? Of(Subscription subscription, Plan plan, string currency)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(plan);
        var period = subscription.Terms.Period;
        var price = plan.Prices.FirstOrDefault(p => p.Period == period && p.Currency == currency)
            ?? plan.Prices.FirstOrDefault(p => p.Period == period);
        if (!IsBilled(subscription.Status))
        {
            return Money.Zero(Currency.Of(price?.Currency ?? currency));
        }

        if (price is null)
        {
            return null;
        }

        var perPeriod = Money.Of(price.PerSite, price.Currency) * subscription.Terms.Sites
            + Money.Of(price.ExtraAsset ?? 0m, price.Currency) * subscription.Terms.ExtraAssets;
        return (period == BillingPeriod.Annual ? perPeriod * (1m / 12m) : perPeriod).Round();
    }

    public static bool IsBilled(SubscriptionStatus status) =>
        status is SubscriptionStatus.Active or SubscriptionStatus.PastDue or SubscriptionStatus.Restricted;
}
