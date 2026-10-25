using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Billing.Domain;

public readonly record struct SubscriptionId(Guid Value) : IEntityId<SubscriptionId>
{
    public static SubscriptionId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>Business model §2.5. What each state allows is enforced in the API (US-P1-BIL-05, ADR-0023).</summary>
public enum SubscriptionStatus
{
    Trial = 1,
    Active = 2,
    PastDue = 3,
    Restricted = 4,
    Suspended = 5,
    Cancelled = 6,
    Expired = 7,
    Deleted = 8,
}

/// <summary>
/// What the tenant bought: a plan version, the period, the quantities, add-ons and what sales granted on top
/// (<c>ExtraFeatures</c>, <c>LimitOverrides</c>: a limit set here replaces the plan's). Written to the ledger as is.
/// </summary>
public sealed record SubscriptionTerms(
    Guid PlanId,
    string PlanCode,
    int PlanVersion,
    BillingPeriod Period,
    int Sites,
    int ExtraAssets,
    IReadOnlyDictionary<string, int> AddOns,
    IReadOnlyList<string> ExtraFeatures,
    IReadOnlyDictionary<string, int> LimitOverrides)
{
    public const int MaxSites = 10_000;

    public static SubscriptionTerms Of(Plan plan, BillingPeriod period, int sites = 1, int extraAssets = 0, IReadOnlyDictionary<string, int>? addOns = null,
        IEnumerable<string>? extraFeatures = null, IReadOnlyDictionary<string, int>? limitOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(plan.Id.Value, plan.Code, plan.Version, period, sites, extraAssets,
            Sorted(addOns), [.. (extraFeatures ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], Sorted(limitOverrides));
    }

    /// <summary>The shape rules; plan, add-on, feature and limit codes must be known.</summary>
    public Error? Validate()
    {
        if (!Enum.IsDefined(Period))
        {
            return Error.Validation("billing.period_invalid", "The billing period is monthly or annual.");
        }

        if (Sites is < 1 or > MaxSites || ExtraAssets < 0)
        {
            return Error.Validation("billing.quantity_invalid", $"A subscription has 1–{MaxSites} sites and zero or more extra assets.");
        }

        if (AddOns.FirstOrDefault(a => !SharedKernel.Entitlements.AddOns.IsKnown(a.Key) || a.Value < 1) is { Key: not null } addOn)
        {
            return Error.Validation("billing.add_on_invalid", $"Unknown add-on '{addOn.Key}' or a quantity below 1.");
        }

        if (ExtraFeatures.FirstOrDefault(f => !Features.IsKnown(f)) is { } feature)
        {
            return Error.Validation("billing.feature_unknown", $"Unknown feature '{feature}'.");
        }

        return LimitOverrides.FirstOrDefault(l => !Limits.IsKnown(l.Key) || l.Value < 0) is { Key: not null } limit
            ? Error.Validation("billing.limit_unknown", $"Unknown limit '{limit.Key}' or a value below zero.")
            : null;
    }

    private static Dictionary<string, int> Sorted(IReadOnlyDictionary<string, int>? values) =>
        (values ?? new Dictionary<string, int>()).OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
}

[EventType("billing.subscription_started.v1")]
public sealed record SubscriptionStarted(Guid SubscriptionId, SubscriptionTerms Terms, string Status, DateTimeOffset? TrialEndsAt, string? Actor) : DomainEvent;

/// <summary><c>Reason</c>: <c>operator</c>, <c>payment_recorded</c>, or the time rule that moved it (e.g. <c>unpaid_14_days</c>).</summary>
[EventType("billing.subscription_status_changed.v1")]
public sealed record SubscriptionStatusChanged(Guid SubscriptionId, string From, string To, string Reason, string? Actor) : DomainEvent;

[EventType("billing.subscription_terms_changed.v1")]
public sealed record SubscriptionTermsChanged(Guid SubscriptionId, SubscriptionTerms Before, SubscriptionTerms After, string? Actor) : DomainEvent;

/// <summary>
/// The one subscription of a tenant (US-P1-BIL-02, ADR-0023) and its <b>closed</b> state machine (business model
/// §2.5): only the transitions of <see cref="Allowed"/> exist; anything else is refused (TC-P1-BIL-006). Operators
/// move it (activate, missed payment, payment, cancel) until the billing provider of Phase 10 does; the time rules
/// are applied by <see cref="Advance"/>, which a job calls. Deleted is only a state here: data deletion is built
/// with tenant offboarding.
/// </summary>
public sealed class Subscription : AggregateRoot<SubscriptionId>, ITenantOwned
{
    public const int TrialDays = 30;
    public const int RestrictedAfterUnpaidDays = 14;
    public const int SuspendedAfterUnpaidDays = 30;
    public const int CancelledAfterUnpaidDays = 60;
    public const int ReactivationDays = 60;
    public const int DeletedAfterExpiredDays = 60;
    public const int DeletedAfterCancelledDays = 90;

    /// <summary>The transitions of business model §2.5, and only those.</summary>
    public static readonly IReadOnlySet<(SubscriptionStatus From, SubscriptionStatus To)> Allowed = new HashSet<(SubscriptionStatus, SubscriptionStatus)>
    {
        (SubscriptionStatus.Trial, SubscriptionStatus.Active),
        (SubscriptionStatus.Trial, SubscriptionStatus.Expired),
        (SubscriptionStatus.Expired, SubscriptionStatus.Active),
        (SubscriptionStatus.Expired, SubscriptionStatus.Deleted),
        (SubscriptionStatus.Active, SubscriptionStatus.PastDue),
        (SubscriptionStatus.Active, SubscriptionStatus.Cancelled),
        (SubscriptionStatus.PastDue, SubscriptionStatus.Active),
        (SubscriptionStatus.PastDue, SubscriptionStatus.Restricted),
        (SubscriptionStatus.Restricted, SubscriptionStatus.Active),
        (SubscriptionStatus.Restricted, SubscriptionStatus.Suspended),
        (SubscriptionStatus.Suspended, SubscriptionStatus.Active),
        (SubscriptionStatus.Suspended, SubscriptionStatus.Cancelled),
        (SubscriptionStatus.Cancelled, SubscriptionStatus.Deleted),
    };

    private Subscription(SubscriptionId id, TenantId tenantId, SubscriptionTerms terms, DateTimeOffset createdAt)
        : base(id)
    {
        TenantId = tenantId;
        Terms = terms;
        Status = SubscriptionStatus.Trial;
        StatusSince = createdAt;
        TrialEndsAt = createdAt.AddDays(TrialDays);
        CreatedAt = createdAt;
    }

    public TenantId TenantId { get; private set; }

    public SubscriptionTerms Terms { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public DateTimeOffset StatusSince { get; private set; }

    public DateTimeOffset? TrialEndsAt { get; private set; }

    /// <summary>When the first unpaid payment was recorded (PastDue and later); null once paid.</summary>
    public DateTimeOffset? UnpaidSince { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>A new tenant's subscription: a <see cref="TrialDays"/>-day trial on the given plan, one site, monthly.</summary>
    public static Subscription StartTrial(TimeProvider clock, TenantId tenant, Plan plan, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        var subscription = new Subscription(EntityId.New<SubscriptionId>(clock), tenant, SubscriptionTerms.Of(plan, BillingPeriod.Monthly), now);
        subscription.Raise(new SubscriptionStarted(subscription.Id.Value, subscription.Terms, Name(subscription.Status), subscription.TrialEndsAt, actor)
        {
            OccurredAt = now,
        });
        return subscription;
    }

    public static string Name(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.PastDue => "past_due",
        _ => status.ToString().ToLowerInvariant(),
    };

    /// <summary>The trial converts, or an expired trial comes back within <see cref="ReactivationDays"/> days (data kept).</summary>
    public Result<bool> Activate(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status is not (SubscriptionStatus.Trial or SubscriptionStatus.Expired))
        {
            // PastDue → Active exists, but through a payment, not by activating.
            return Invalid(Status, SubscriptionStatus.Active);
        }

        if (Status == SubscriptionStatus.Expired && clock.GetUtcNow() >= StatusSince.AddDays(ReactivationDays))
        {
            return Error.BusinessRule("billing.reactivation_window_passed", $"An expired trial can be reactivated within {ReactivationDays} days only.");
        }

        return MoveTo(SubscriptionStatus.Active, "operator", actor, clock.GetUtcNow());
    }

    /// <summary>A payment failed or an invoice is overdue: Active → PastDue, and the unpaid days start counting.</summary>
    public Result<bool> RecordMissedPayment(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var moved = MoveTo(SubscriptionStatus.PastDue, "operator", actor, clock.GetUtcNow());
        if (moved.IsSuccess)
        {
            UnpaidSince = clock.GetUtcNow();
        }

        return moved;
    }

    /// <summary>The tenant paid: back to Active from PastDue, Restricted or Suspended; nothing to do when Active or Trial.</summary>
    public Result<bool> RecordPayment(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status is SubscriptionStatus.Active or SubscriptionStatus.Trial)
        {
            return false;
        }

        if (Status is not (SubscriptionStatus.PastDue or SubscriptionStatus.Restricted or SubscriptionStatus.Suspended))
        {
            // Expired → Active exists, but by reactivating the trial, not by a payment.
            return Invalid(Status, SubscriptionStatus.Active);
        }

        var moved = MoveTo(SubscriptionStatus.Active, "payment_recorded", actor, clock.GetUtcNow());
        if (moved.IsSuccess)
        {
            UnpaidSince = null;
        }

        return moved;
    }

    /// <summary>The customer cancels (Active), or an operator ends a suspended subscription.</summary>
    public Result<bool> Cancel(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Status is SubscriptionStatus.Active or SubscriptionStatus.Suspended
            ? MoveTo(SubscriptionStatus.Cancelled, "operator", actor, clock.GetUtcNow())
            : Invalid(Status, SubscriptionStatus.Cancelled);
    }

    /// <summary>
    /// Applies the time rules of §2.5 up to now (several at once when the job was down): trial end, unpaid days,
    /// and the retention periods before deletion. Returns whether anything changed.
    /// </summary>
    public bool Advance(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        var changed = false;
        while (Due(now) is { } next)
        {
            MoveTo(next.To, next.Reason, actor: null, next.At);
            changed = true;
        }

        return changed;
    }

    /// <summary>New terms (plan version, period, quantities, add-ons, overrides) while the subscription is in use.</summary>
    public Result<bool> ChangeTerms(TimeProvider clock, SubscriptionTerms terms, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Validate() is { } invalid)
        {
            return invalid;
        }

        if (Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired or SubscriptionStatus.Deleted)
        {
            return Error.BusinessRule("billing.subscription_closed", "The terms of a cancelled, expired or deleted subscription cannot change.");
        }

        if (Equivalent(Terms, terms))
        {
            return false;
        }

        var before = Terms;
        Terms = terms;
        Raise(new SubscriptionTermsChanged(Id.Value, before, terms, actor) { OccurredAt = clock.GetUtcNow() });
        return true;
    }

    private (SubscriptionStatus To, string Reason, DateTimeOffset At)? Due(DateTimeOffset now)
    {
        (SubscriptionStatus To, string Reason, DateTimeOffset At)? next = Status switch
        {
            SubscriptionStatus.Trial when TrialEndsAt is { } end => (SubscriptionStatus.Expired, "trial_ended", end),
            SubscriptionStatus.PastDue => (SubscriptionStatus.Restricted, $"unpaid_{RestrictedAfterUnpaidDays}_days", Unpaid(RestrictedAfterUnpaidDays)),
            SubscriptionStatus.Restricted => (SubscriptionStatus.Suspended, $"unpaid_{SuspendedAfterUnpaidDays}_days", Unpaid(SuspendedAfterUnpaidDays)),
            SubscriptionStatus.Suspended => (SubscriptionStatus.Cancelled, $"unpaid_{CancelledAfterUnpaidDays}_days", Unpaid(CancelledAfterUnpaidDays)),
            SubscriptionStatus.Expired => (SubscriptionStatus.Deleted, $"expired_{DeletedAfterExpiredDays}_days", StatusSince.AddDays(DeletedAfterExpiredDays)),
            SubscriptionStatus.Cancelled => (SubscriptionStatus.Deleted, $"cancelled_{DeletedAfterCancelledDays}_days", StatusSince.AddDays(DeletedAfterCancelledDays)),
            _ => null,
        };
        return next is { } due && now >= due.At ? due : null;
    }

    private DateTimeOffset Unpaid(int days) => (UnpaidSince ?? StatusSince).AddDays(days);

    private Result<bool> MoveTo(SubscriptionStatus to, string reason, string? actor, DateTimeOffset at)
    {
        if (!Allowed.Contains((Status, to)))
        {
            return Invalid(Status, to);
        }

        var from = Status;
        Status = to;
        StatusSince = at;
        if (to == SubscriptionStatus.Active)
        {
            TrialEndsAt = null;
        }

        Raise(new SubscriptionStatusChanged(Id.Value, Name(from), Name(to), reason, actor) { OccurredAt = at });
        return true;
    }

    private static Error Invalid(SubscriptionStatus from, SubscriptionStatus to) =>
        Error.BusinessRule("billing.transition_invalid", $"A subscription cannot go from {Name(from)} to {Name(to)}.");

    private static bool Equivalent(SubscriptionTerms a, SubscriptionTerms b) =>
        a.PlanId == b.PlanId && a.Period == b.Period && a.Sites == b.Sites && a.ExtraAssets == b.ExtraAssets
        && a.AddOns.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(b.AddOns.OrderBy(x => x.Key, StringComparer.Ordinal))
        && a.ExtraFeatures.SequenceEqual(b.ExtraFeatures, StringComparer.Ordinal)
        && a.LimitOverrides.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(b.LimitOverrides.OrderBy(x => x.Key, StringComparer.Ordinal));
}
