using Karry.SharedKernel.Entitlements;

namespace Karry.Modules.Billing.Domain;

/// <summary>
/// Plan version + add-ons + overrides → what the tenant may use (ADR-0023, TC-P1-BIL-001). Features: the plan's,
/// those of its add-ons, and the extra features sales granted. Limits: the plan's, raised by add-ons per unit, then
/// replaced by an override. A tenant without a subscription (provisioning still running) gets a trial of the given plan.
/// </summary>
public static class EntitlementRules
{
    public static TenantEntitlements Resolve(Plan plan, Subscription? subscription)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var terms = subscription?.Terms ?? SubscriptionTerms.Of(plan, BillingPeriod.Monthly);
        if (plan.Id.Value != terms.PlanId)
        {
            throw new ArgumentException("The plan is not the subscription's plan version.", nameof(plan));
        }

        var features = new HashSet<string>(plan.Features, StringComparer.Ordinal);
        features.UnionWith(terms.AddOns.Keys.SelectMany(AddOns.FeaturesOf));
        features.UnionWith(terms.ExtraFeatures);

        var limits = new Dictionary<string, int>(plan.Limits, StringComparer.Ordinal);
        foreach (var (addOn, quantity) in terms.AddOns)
        {
            foreach (var limit in limits.Keys.ToList())
            {
                limits[limit] += AddOns.LimitIncrease(addOn, limit) * quantity;
            }
        }

        foreach (var (limit, value) in terms.LimitOverrides)
        {
            limits[limit] = value;
        }

        return new TenantEntitlements(plan.Code, plan.Version, Subscription.Name(subscription?.Status ?? SubscriptionStatus.Trial),
            subscription?.TrialEndsAt, features, limits, terms.Sites, terms.ExtraAssets);
    }
}
