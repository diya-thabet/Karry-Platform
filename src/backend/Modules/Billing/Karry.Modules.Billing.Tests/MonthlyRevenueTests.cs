using Karry.Modules.Billing.Domain;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Monetary;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Billing.Tests;

/// <summary>MRR of one subscription for the operator console (US-P1-OPS-03 AC1, ADR-0029 §3).</summary>
[Trait("Story", "US-P1-OPS-03")]
public sealed class MonthlyRevenueTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.Zero));

    private static Plan Priced { get; } = Plan.Publish(new FakeTimeProvider(), "professional", null, "Professional",
        [Features.FieldOperations], new Dictionary<string, int>(),
        [
            new PlanPrice("EUR", BillingPeriod.Monthly, 590m, 5m), new PlanPrice("EUR", BillingPeriod.Annual, 5900m, 50m),
            new PlanPrice("TND", BillingPeriod.Monthly, 1990m, 15m),
        ], actor: null).Value;

    private static Plan Quoted { get; } = Plan.Publish(new FakeTimeProvider(), "enterprise", null, "Enterprise",
        [Features.FieldOperations], new Dictionary<string, int>(), [], actor: null).Value;

    [Fact]
    public void A_monthly_subscription_bills_its_sites_and_extra_machines_each_month()
    {
        var subscription = Active(Priced, BillingPeriod.Monthly, sites: 2, extraAssets: 3);

        MonthlyRevenue.Of(subscription, Priced, "EUR").Should().Be(Money.Of(1195m, "EUR"));
    }

    [Fact]
    public void An_annual_price_is_spread_over_twelve_months_and_rounded_to_cents()
    {
        var subscription = Active(Priced, BillingPeriod.Annual, sites: 2, extraAssets: 3);

        MonthlyRevenue.Of(subscription, Priced, "EUR").Should().Be(Money.Of(995.83m, "EUR"), "(2 × 5900 + 3 × 50) / 12 = 995.833…");
    }

    [Fact]
    public void The_company_s_currency_is_used_when_the_plan_prices_it_else_the_plan_s_first_currency()
    {
        var subscription = Active(Priced, BillingPeriod.Monthly, sites: 1, extraAssets: 0);

        MonthlyRevenue.Of(subscription, Priced, "TND").Should().Be(Money.Of(1990m, "TND"));
        MonthlyRevenue.Of(subscription, Priced, "USD").Should().Be(Money.Of(590m, "EUR"));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Trial, false)]
    [InlineData(SubscriptionStatus.Active, true)]
    [InlineData(SubscriptionStatus.PastDue, true)]
    [InlineData(SubscriptionStatus.Restricted, true)]
    [InlineData(SubscriptionStatus.Suspended, false)]
    [InlineData(SubscriptionStatus.Cancelled, false)]
    [InlineData(SubscriptionStatus.Expired, false)]
    [InlineData(SubscriptionStatus.Deleted, false)]
    public void Only_a_company_that_is_billed_brings_revenue(SubscriptionStatus state, bool billed)
    {
        var subscription = In(state);

        MonthlyRevenue.IsBilled(state).Should().Be(billed);
        MonthlyRevenue.Of(subscription, Priced, "EUR").Should().Be(Money.Of(billed ? 590m : 0m, "EUR"));
    }

    [Fact]
    public void A_plan_without_prices_has_no_revenue_figure_once_billed_and_zero_before()
    {
        var trial = Subscription.StartTrial(clock, Tenant, Quoted, actor: null);
        var billed = Subscription.StartTrial(clock, Tenant, Quoted, actor: null);
        billed.Activate(clock, "op");

        MonthlyRevenue.Of(trial, Quoted, "EUR").Should().Be(Money.Zero(Currency.Of("EUR")));
        MonthlyRevenue.Of(billed, Quoted, "EUR").Should().BeNull("an enterprise plan is quoted by sales");
    }

    private Subscription Active(Plan plan, BillingPeriod period, int sites, int extraAssets)
    {
        var subscription = Subscription.StartTrial(clock, Tenant, plan, actor: null);
        subscription.Activate(clock, "op").IsSuccess.Should().BeTrue();
        subscription.ChangeTerms(clock, SubscriptionTerms.Of(plan, period, sites, extraAssets), "op").IsSuccess.Should().BeTrue();
        return subscription;
    }

    private Subscription In(SubscriptionStatus state)
    {
        var subscription = Subscription.StartTrial(clock, Tenant, Priced, actor: null);
        void Days(int days)
        {
            clock.Advance(TimeSpan.FromDays(days));
            subscription.Advance(clock);
        }

        if (state is not (SubscriptionStatus.Trial or SubscriptionStatus.Expired))
        {
            subscription.Activate(clock, "op");
        }

        switch (state)
        {
            case SubscriptionStatus.Expired:
                Days(30);
                break;
            case SubscriptionStatus.PastDue:
                subscription.RecordMissedPayment(clock, "op");
                break;
            case SubscriptionStatus.Restricted:
                subscription.RecordMissedPayment(clock, "op");
                Days(14);
                break;
            case SubscriptionStatus.Suspended:
                subscription.RecordMissedPayment(clock, "op");
                Days(30);
                break;
            case SubscriptionStatus.Cancelled:
                subscription.Cancel(clock, "op");
                break;
            case SubscriptionStatus.Deleted:
                subscription.Cancel(clock, "op");
                Days(90);
                break;
        }

        subscription.Status.Should().Be(state, "the test setup must reach the state");
        return subscription;
    }
}
