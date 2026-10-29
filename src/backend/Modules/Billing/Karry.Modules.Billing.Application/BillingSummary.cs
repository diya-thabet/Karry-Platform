using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Billing.Domain;

namespace Karry.Modules.Billing.Application;

/// <summary>Billing's columns of the operator console (ADR-0029 §3): plan, subscription state, trial end and MRR.</summary>
public sealed class BillingSummary(ISubscriptionStore subscriptions, IPlanStore plans) : ITenantSummarySource
{
    public async Task<TenantSummaryPart> SummarizeAsync(TenantSummaryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subscription = await subscriptions.FindAsync(cancellationToken).ConfigureAwait(false);
        if (subscription is null)
        {
            return new TenantSummaryPart();
        }

        var plan = (await plans.VersionsAsync(subscription.Terms.PlanCode, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p => p.Id.Value == subscription.Terms.PlanId);
        var mrr = plan is null ? null : MonthlyRevenue.Of(subscription, plan, request.Currency);
        return new TenantSummaryPart(
            subscription.Terms.PlanCode, subscription.Terms.PlanVersion, Subscription.Name(subscription.Status), subscription.TrialEndsAt,
            mrr?.Amount, mrr?.Currency.Code);
    }
}
