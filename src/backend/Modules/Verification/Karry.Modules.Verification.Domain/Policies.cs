using System.Globalization;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Verification.Domain;

public readonly record struct ReviewPolicyId(Guid Value) : IEntityId<ReviewPolicyId>
{
    public static ReviewPolicyId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// The rules a company reviews one record type by (US-P2-VER-05, ADR-0037 §5): auto-approval of unflagged records, the
/// four-eyes thresholds of a correction, the SLA and who an overdue item escalates to.
/// </summary>
public sealed record PolicyTerms(bool AutoApprove, decimal FourEyesHours, decimal FourEyesShare, int SlaHours, string EscalateTo)
{
    /// <summary>What a company reviews by until it publishes its own version (version 0).</summary>
    public static PolicyTerms Defaults { get; } = new(AutoApprove: false, FourEyesHours: 2m, FourEyesShare: 0.05m, SlaHours: 24, EscalateTo: "site-manager");

    /// <summary>The roles an overdue item may escalate to.</summary>
    public static IReadOnlyList<string> EscalationRoles { get; } = ["site-manager", "tenant-admin", "executive"];

    /// <summary>The first thing wrong with the terms, or null.</summary>
    public Error? Check()
    {
        if (FourEyesHours is < 0m or > 24m || decimal.Round(FourEyesHours, 1) != FourEyesHours)
        {
            return ReviewPolicyRules.Invalid("fourEyesHours", "0 to 24 hours, one decimal");
        }

        if (FourEyesShare is < 0m or > 1m || decimal.Round(FourEyesShare, 2) != FourEyesShare)
        {
            return ReviewPolicyRules.Invalid("fourEyesShare", "0 to 1, two decimals");
        }

        if (SlaHours is < 1 or > 168)
        {
            return ReviewPolicyRules.Invalid("slaHours", "1 to 168 hours");
        }

        return EscalationRoles.Contains(EscalateTo) ? null : ReviewPolicyRules.Invalid("escalateTo", string.Join(", ", EscalationRoles));
    }
}

[EventType("verification.review_policy_published.v1")]
[AuditSubject("review_policy", nameof(PolicyId))]
public sealed record ReviewPolicyPublished(
    Guid PolicyId, string RecordType, int Version, DateTimeOffset EffectiveFrom, bool AutoApprove, decimal FourEyesHours, decimal FourEyesShare, int SlaHours,
    string EscalateTo, string? Actor) : DomainEvent;

/// <summary>
/// One published version of a company's review policy for a record type (ADR-0037 §5): never changed; a new version
/// takes effect from its date, and every item keeps the version in force when it was created (TC-P2-VER-007).
/// </summary>
public sealed class ReviewPolicy : AggregateRoot<ReviewPolicyId>, ITenantOwned
{
    private ReviewPolicy(ReviewPolicyId id)
        : base(id)
    {
    }

    // EF Core
    private ReviewPolicy()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public string RecordType { get; private set; } = null!;

    /// <summary>1, 2, … per record type; 0 is the defaults, never stored.</summary>
    public int Version { get; private set; }

    public DateTimeOffset EffectiveFrom { get; private set; }

    public bool AutoApprove { get; private set; }

    public decimal FourEyesHours { get; private set; }

    public decimal FourEyesShare { get; private set; }

    public int SlaHours { get; private set; }

    public string EscalateTo { get; private set; } = null!;

    public DateTimeOffset PublishedAt { get; private set; }

    /// <summary>Who published it: named on every auto-approval made under it (threat T-13).</summary>
    public string? PublishedBy { get; private set; }

    public PolicyTerms Terms => new(AutoApprove, FourEyesHours, FourEyesShare, SlaHours, EscalateTo);

    public static Result<ReviewPolicy> Publish(
        TimeProvider clock, TenantId tenant, string recordType, int version, PolicyTerms terms, DateTimeOffset? effectiveFrom, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Check() is { } invalid)
        {
            return invalid;
        }

        var at = clock.GetUtcNow();
        var from = effectiveFrom ?? at;
        if (from < at.AddMinutes(-1))
        {
            return ReviewPolicyRules.Backdated;
        }

        var policy = new ReviewPolicy(EntityId.New<ReviewPolicyId>(clock))
        {
            TenantId = tenant,
            RecordType = recordType,
            Version = version,
            EffectiveFrom = from,
            AutoApprove = terms.AutoApprove,
            FourEyesHours = terms.FourEyesHours,
            FourEyesShare = terms.FourEyesShare,
            SlaHours = terms.SlaHours,
            EscalateTo = terms.EscalateTo,
            PublishedAt = at,
            PublishedBy = actor,
        };
        policy.Raise(new ReviewPolicyPublished(
            policy.Id.Value, recordType, version, from, terms.AutoApprove, terms.FourEyesHours, terms.FourEyesShare, terms.SlaHours, terms.EscalateTo, actor)
        { OccurredAt = at });
        return policy;
    }
}

/// <summary>The policy in force for an item: its version (0: the defaults), its terms and who published it.</summary>
public sealed record EffectivePolicy(int Version, PolicyTerms Terms, string? PublishedBy)
{
    public static EffectivePolicy Defaults { get; } = new(0, PolicyTerms.Defaults, null);

    /// <summary>The actor of an auto-approval: the policy version that decided (ADR-0037 §5).</summary>
    public string Actor => string.Create(CultureInfo.InvariantCulture, $"policy:{Version}");
}

public static class ReviewPolicyRules
{
    public static readonly Error Backdated = Error.Validation("verification.policy_backdated", "A policy takes effect now or later, never in the past.");

    public static readonly Error UnknownRecordType = Error.NotFound("verification.record_type_unknown", "No such record type to review.");

    public static Error Invalid(string field, string expected) => Error.Validation("verification.policy_invalid", $"{field}: {expected}.");
}
