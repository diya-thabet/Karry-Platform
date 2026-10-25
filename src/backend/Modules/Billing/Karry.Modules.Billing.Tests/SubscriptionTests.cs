using Karry.Modules.Billing.Domain;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Billing.Tests;

/// <summary>
/// The subscription state machine of business model §2.5 (US-P1-BIL-02 AC2, ADR-0023): only its transitions exist
/// (TC-P1-BIL-006), the time rules move it, and its terms change only while it is in use.
/// </summary>
[Trait("Story", "US-P1-BIL-02")]
public sealed class SubscriptionTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));

    private static Plan Professional { get; } = Plan.Publish(new FakeTimeProvider(), "professional", null, "Professional",
        [Features.FieldOperations, Features.Maintenance], new Dictionary<string, int> { [Limits.OfficeUsers] = 15 }, [], actor: null).Value;

    public static TheoryData<SubscriptionStatus> States() => [.. Enum.GetValues<SubscriptionStatus>()];

    private (string Name, SubscriptionStatus[] From, SubscriptionStatus[] NoOp, SubscriptionStatus Target, Func<Subscription, Result<bool>> Act)[] Actions() =>
    [
        ("activate", [SubscriptionStatus.Trial, SubscriptionStatus.Expired], [], SubscriptionStatus.Active, s => s.Activate(clock, "op")),
        ("missed payment", [SubscriptionStatus.Active], [], SubscriptionStatus.PastDue, s => s.RecordMissedPayment(clock, "op")),
        ("payment", [SubscriptionStatus.PastDue, SubscriptionStatus.Restricted, SubscriptionStatus.Suspended],
            [SubscriptionStatus.Trial, SubscriptionStatus.Active], SubscriptionStatus.Active, s => s.RecordPayment(clock, "op")),
        ("cancel", [SubscriptionStatus.Active, SubscriptionStatus.Suspended], [], SubscriptionStatus.Cancelled, s => s.Cancel(clock, "op")),
    ];

    [Fact]
    public void A_new_tenant_gets_a_30_day_trial_of_one_site_monthly()
    {
        var subscription = Subscription.StartTrial(clock, Tenant, Professional, actor: null);

        subscription.Status.Should().Be(SubscriptionStatus.Trial);
        subscription.TrialEndsAt.Should().Be(clock.GetUtcNow().AddDays(30));
        subscription.Terms.PlanCode.Should().Be("professional");
        subscription.Terms.Sites.Should().Be(1);
        subscription.Terms.Period.Should().Be(BillingPeriod.Monthly);
        subscription.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<SubscriptionStarted>();
    }

    [Theory]
    [MemberData(nameof(States))]
    [Trait("TestCase", "TC-P1-BIL-006")]
    public void Every_operator_action_works_only_from_its_own_states_and_only_along_the_table(SubscriptionStatus state)
    {
        foreach (var action in Actions())
        {
            var subscription = In(state);
            var result = action.Act(subscription);

            if (action.From.Contains(state))
            {
                Subscription.Allowed.Should().Contain((state, action.Target), $"{action.Name} from {state} must be a transition of the table");
                result.Value.Should().BeTrue($"{action.Name} works from {state}");
                subscription.Status.Should().Be(action.Target);
            }
            else if (action.NoOp.Contains(state))
            {
                result.Value.Should().BeFalse($"{action.Name} from {state} changes nothing");
                subscription.Status.Should().Be(state);
            }
            else
            {
                result.Error!.Code.Should().Be("billing.transition_invalid", $"{action.Name} is refused from {state}");
                subscription.Status.Should().Be(state);
            }
        }
    }

    [Fact]
    [Trait("TestCase", "TC-P1-BIL-006")]
    public void Every_transition_of_the_table_is_reachable_by_an_action_or_a_time_rule()
    {
        var timeRules = new HashSet<(SubscriptionStatus, SubscriptionStatus)>
        {
            (SubscriptionStatus.Trial, SubscriptionStatus.Expired),
            (SubscriptionStatus.PastDue, SubscriptionStatus.Restricted),
            (SubscriptionStatus.Restricted, SubscriptionStatus.Suspended),
            (SubscriptionStatus.Suspended, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.Expired, SubscriptionStatus.Deleted),
            (SubscriptionStatus.Cancelled, SubscriptionStatus.Deleted),
        };
        var byActions = Actions().SelectMany(a => a.From.Select(from => (from, a.Target)));

        timeRules.Concat(byActions).Distinct().Should().BeEquivalentTo(Subscription.Allowed, "no transition of the table is dead, none is extra");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-BIL-006")]
    public void The_table_is_exactly_the_lifecycle_of_the_business_model()
    {
        Subscription.Allowed.Should().HaveCount(13);
        Subscription.Allowed.Should().NotContain(t => t.To == SubscriptionStatus.Trial, "nothing goes back to a trial");
        Subscription.Allowed.Should().NotContain(t => t.From == SubscriptionStatus.Deleted, "deleted is final");
        Subscription.Allowed.Should().NotContain((SubscriptionStatus.Cancelled, SubscriptionStatus.Active));
        Subscription.Allowed.Should().NotContain((SubscriptionStatus.Trial, SubscriptionStatus.PastDue));
    }

    [Fact]
    public void Unpaid_days_restrict_then_suspend_then_cancel_and_cancelled_data_goes_after_90_days()
    {
        var subscription = In(SubscriptionStatus.Active);
        subscription.RecordMissedPayment(clock, "op");

        StatusAfter(subscription, days: 13).Should().Be(SubscriptionStatus.PastDue);
        StatusAfter(subscription, days: 1).Should().Be(SubscriptionStatus.Restricted, "14 days unpaid");
        StatusAfter(subscription, days: 16).Should().Be(SubscriptionStatus.Suspended, "30 days unpaid");
        StatusAfter(subscription, days: 30).Should().Be(SubscriptionStatus.Cancelled, "60 days unpaid");
        StatusAfter(subscription, days: 89).Should().Be(SubscriptionStatus.Cancelled);
        StatusAfter(subscription, days: 1).Should().Be(SubscriptionStatus.Deleted, "90 days after cancellation");
    }

    [Fact]
    public void A_late_run_applies_every_rule_that_fell_due_in_order()
    {
        var subscription = In(SubscriptionStatus.Active);
        subscription.RecordMissedPayment(clock, "op");
        subscription.ClearDomainEvents();

        clock.Advance(TimeSpan.FromDays(61));
        subscription.Advance(clock).Should().BeTrue();

        subscription.Status.Should().Be(SubscriptionStatus.Cancelled);
        subscription.DomainEvents.OfType<SubscriptionStatusChanged>().Select(e => e.Reason)
            .Should().Equal("unpaid_14_days", "unpaid_30_days", "unpaid_60_days");
        subscription.Advance(clock).Should().BeFalse("nothing more is due");
    }

    [Fact]
    public void A_trial_expires_at_its_end_and_can_come_back_within_60_days()
    {
        var subscription = In(SubscriptionStatus.Trial);

        StatusAfter(subscription, days: 30).Should().Be(SubscriptionStatus.Expired);
        clock.Advance(TimeSpan.FromDays(59));
        subscription.Activate(clock, "op").IsSuccess.Should().BeTrue();
        subscription.Status.Should().Be(SubscriptionStatus.Active);
        subscription.TrialEndsAt.Should().BeNull("an active subscription has no trial end");

        var late = In(SubscriptionStatus.Expired);
        clock.Advance(TimeSpan.FromDays(60));
        late.Activate(clock, "op").Error!.Code.Should().Be("billing.reactivation_window_passed");
    }

    [Fact]
    public void A_payment_brings_a_subscription_waiting_for_money_back_and_stops_the_unpaid_clock()
    {
        foreach (var state in new[] { SubscriptionStatus.PastDue, SubscriptionStatus.Restricted, SubscriptionStatus.Suspended })
        {
            var subscription = In(state);

            subscription.RecordPayment(clock, "op").Value.Should().BeTrue();

            subscription.Status.Should().Be(SubscriptionStatus.Active);
            subscription.UnpaidSince.Should().BeNull();
            subscription.DomainEvents.OfType<SubscriptionStatusChanged>().Last().Reason.Should().Be("payment_recorded");
        }
    }

    [Fact]
    public void Terms_change_with_before_and_after_and_only_while_the_subscription_is_in_use()
    {
        var subscription = In(SubscriptionStatus.Active);
        var terms = SubscriptionTerms.Of(Professional, BillingPeriod.Annual, sites: 3, addOns: new Dictionary<string, int> { [AddOns.AiCopilot] = 1 });

        subscription.ChangeTerms(clock, terms, "op").Value.Should().BeTrue();
        var changed = subscription.DomainEvents.OfType<SubscriptionTermsChanged>().Single();
        changed.Before.Sites.Should().Be(1);
        changed.After.Sites.Should().Be(3);
        subscription.ChangeTerms(clock, terms with { }, "op").Value.Should().BeFalse("the same terms change nothing");

        subscription.ChangeTerms(clock, terms with { Sites = 0 }, "op").Error!.Code.Should().Be("billing.quantity_invalid");
        subscription.ChangeTerms(clock, terms with { AddOns = new Dictionary<string, int> { ["jetpack"] = 1 } }, "op").Error!.Code.Should().Be("billing.add_on_invalid");
        subscription.ChangeTerms(clock, terms with { ExtraFeatures = ["teleportation"] }, "op").Error!.Code.Should().Be("billing.feature_unknown");

        var cancelled = In(SubscriptionStatus.Cancelled);
        cancelled.ChangeTerms(clock, terms, "op").Error!.Code.Should().Be("billing.subscription_closed");
    }

    [Fact]
    public void An_invoice_has_a_number_an_exact_amount_and_coherent_dates_and_is_paid_once()
    {
        var subscription = In(SubscriptionStatus.Active);
        var issued = new DateOnly(2026, 10, 1);

        Invoice.Record(clock, subscription, " ", 590m, "EUR", issued, issued, "op").Error!.Code.Should().Be("billing.invoice_number_invalid");
        Invoice.Record(clock, subscription, "K-1", 590.001m, "EUR", issued, issued, "op").Error!.Code.Should().Be("billing.invoice_amount_invalid");
        Invoice.Record(clock, subscription, "K-1", 0m, "EUR", issued, issued, "op").Error!.Code.Should().Be("billing.invoice_amount_invalid");
        Invoice.Record(clock, subscription, "K-1", 590m, "EUX", issued, issued, "op").Error!.Code.Should().Be("billing.currency_unsupported");
        Invoice.Record(clock, subscription, "K-1", 590m, "EUR", issued, issued.AddDays(-1), "op").Error!.Code.Should().Be("billing.invoice_dates_invalid");

        var invoice = Invoice.Record(clock, subscription, "K-1", 590m, "EUR", issued, issued.AddDays(30), "op").Value;
        invoice.MarkPaid(clock, issued.AddDays(10), "op").Value.Should().BeTrue();
        invoice.MarkPaid(clock, issued.AddDays(10), "op").Value.Should().BeFalse("the same payment again is a no-op");
        invoice.MarkPaid(clock, issued.AddDays(11), "op").Error!.Code.Should().Be("billing.invoice_already_paid");
    }

    private SubscriptionStatus StatusAfter(Subscription subscription, int days)
    {
        clock.Advance(TimeSpan.FromDays(days));
        subscription.Advance(clock);
        return subscription.Status;
    }

    /// <summary>A subscription brought to <paramref name="state"/> by the public operations only.</summary>
    private Subscription In(SubscriptionStatus state)
    {
        var subscription = Subscription.StartTrial(clock, Tenant, Professional, actor: null);
        void Days(int days)
        {
            clock.Advance(TimeSpan.FromDays(days));
            subscription.Advance(clock);
        }

        switch (state)
        {
            case SubscriptionStatus.Trial:
                break;
            case SubscriptionStatus.Expired:
                Days(30);
                break;
            case SubscriptionStatus.Active:
                subscription.Activate(clock, "op");
                break;
            case SubscriptionStatus.PastDue:
                subscription.Activate(clock, "op");
                subscription.RecordMissedPayment(clock, "op");
                break;
            case SubscriptionStatus.Restricted:
                subscription.Activate(clock, "op");
                subscription.RecordMissedPayment(clock, "op");
                Days(14);
                break;
            case SubscriptionStatus.Suspended:
                subscription.Activate(clock, "op");
                subscription.RecordMissedPayment(clock, "op");
                Days(30);
                break;
            case SubscriptionStatus.Cancelled:
                subscription.Activate(clock, "op");
                subscription.Cancel(clock, "op");
                break;
            case SubscriptionStatus.Deleted:
                subscription.Activate(clock, "op");
                subscription.Cancel(clock, "op");
                Days(90);
                break;
        }

        subscription.Status.Should().Be(state, "the test setup must reach the state");
        return subscription;
    }
}
