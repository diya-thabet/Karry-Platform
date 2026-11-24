using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Integrity.Domain;

public readonly record struct IntegrityFlagId(Guid Value) : IEntityId<IntegrityFlagId>
{
    /// <summary>UUIDv7 order is creation order: the flag list pages by it (newest first).</summary>
    public static bool operator <(IntegrityFlagId left, IntegrityFlagId right) => left.Value.CompareTo(right.Value) < 0;

    public static bool operator >(IntegrityFlagId left, IntegrityFlagId right) => left.Value.CompareTo(right.Value) > 0;

    public static bool operator <=(IntegrityFlagId left, IntegrityFlagId right) => left.Value.CompareTo(right.Value) <= 0;

    public static bool operator >=(IntegrityFlagId left, IntegrityFlagId right) => left.Value.CompareTo(right.Value) >= 0;

    public static IntegrityFlagId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>An entity a flag is about or linked to (ADR-0036 §2).</summary>
public sealed record SubjectRef(string Type, Guid Id);

[EventType("integrity.flag_opened.v1")]
[AuditSubject("integrity_flag", nameof(FlagId))]
public sealed record FlagOpened(Guid FlagId, string Rule, string Severity, string SubjectType, Guid SubjectId, string Key, Guid? SiteId) : DomainEvent;

[EventType("integrity.flag_closed.v1")]
[AuditSubject("integrity_flag", nameof(FlagId))]
public sealed record FlagClosed(Guid FlagId, string Rule, string Reason, string? Actor) : DomainEvent;

[EventType("integrity.flag_settlement_proposed.v1")]
[AuditSubject("integrity_flag", nameof(FlagId))]
public sealed record FlagSettlementProposed(Guid FlagId, string Rule, string Reason, string? Comment, string? Actor) : DomainEvent;

[EventType("integrity.flag_settlement_declined.v1")]
[AuditSubject("integrity_flag", nameof(FlagId))]
public sealed record FlagSettlementDeclined(Guid FlagId, string Rule, string Reason, string? Comment, string? Actor) : DomainEvent;

/// <summary>
/// Why a continuity gap is settled (US-P2-FO-08 AC1, ADR-0037 §7). A data-entry error is not settled by hand: the entry
/// is corrected (ADR-0037 §1), continuity runs again and the flag closes as re-evaluated.
/// </summary>
public static class SettlementReasons
{
    public const string DataEntryError = "data_entry_error";
    public const string MeterReplaced = "meter_replaced";
    public const string MeterReset = "meter_reset";
    public const string MaintenanceRun = "maintenance_run";
    public const string Towing = "towing";
    public const string Unexplained = "unexplained";

    public static IReadOnlyList<string> All { get; } = [DataEntryError, MeterReplaced, MeterReset, MaintenanceRun, Towing, Unexplained];

    /// <summary>The prefix of a settled flag's close reason (<c>settled:meter_replaced</c>): its key is never raised again.</summary>
    public const string ClosePrefix = "settled:";
}

/// <summary>Why a settlement is refused (ADR-0037 §7).</summary>
public static class SettlementErrors
{
    public const int CommentMax = 500;

    public static readonly Error NotSettleable = Error.BusinessRule("integrity.not_settleable", "Only an open continuity gap (RULE-FO-01) is settled with a reason.");

    public static readonly Error ReasonInvalid = Error.Validation(
        "integrity.settlement_reason_invalid", $"The reason is one of: {string.Join(", ", SettlementReasons.All)}.");

    public static readonly Error CorrectTheEntry = Error.BusinessRule(
        "integrity.correct_the_entry", "A data-entry error is settled by correcting the entry: the gap closes when continuity holds again.");

    public static readonly Error CommentRequired = Error.Validation("integrity.settlement_comment_required", "Say what happened, in at most 500 characters.");

    public static readonly Error AlreadyProposed = Error.Conflict("integrity.settlement_pending", "A settlement of this gap is already waiting for a second approval.");

    public static readonly Error NothingProposed = Error.Conflict("integrity.no_settlement_pending", "No settlement of this gap is waiting for approval.");

    public static readonly Error OwnProposal = Error.Forbidden("integrity.sod_own_settlement", "Another person approves the settlement you proposed.");

    public static readonly Error OwnGap = Error.Forbidden("integrity.sod_own_gap", "You signed one of the entries of this gap: another person settles it.");
}

/// <summary>
/// What a rule found (ADR-0036 §2): opened once per subject, rule and key, closed with a reason, never deleted or
/// reopened (a new finding is a new flag). Severity and the explanation's values are kept as found.
/// </summary>
public sealed class IntegrityFlag : AggregateRoot<IntegrityFlagId>, ITenantOwned
{
    public const string Reevaluated = "re-evaluated";

    /// <summary>Continuity (RULE-FO-01): the only rule a person settles with a reason (ADR-0037 §7).</summary>
    public const string ContinuityRule = "RULE-FO-01";

    /// <summary>FRAUD_SUSPECTED (RULE-FO-11): a gap settled as unexplained or left open beyond 48 h.</summary>
    public const string FraudRule = "RULE-FO-11";

    /// <summary>Why RULE-FO-11 was raised: a person said the gap is unexplained, or nobody settled it in time.</summary>
    public const string FraudUnexplained = "unexplained";

    public const string FraudUnsettled = "unsettled_48h";

    /// <summary>How long a continuity gap may stay open before RULE-FO-11 (US-P2-FO-08 AC2).</summary>
    public static readonly TimeSpan SettleWithin = TimeSpan.FromHours(48);

    private IntegrityFlag(IntegrityFlagId id)
        : base(id)
    {
    }

    // EF Core
    private IntegrityFlag()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public string Rule { get; private set; } = null!;

    public string Severity { get; private set; } = null!;

    public string SubjectType { get; private set; } = null!;

    public Guid SubjectId { get; private set; }

    public string Key { get; private set; } = null!;

    public Guid? SiteId { get; private set; }

    public IReadOnlyList<SubjectRef> Related { get; private set; } = [];

    public IReadOnlyList<string> Evidence { get; private set; } = [];

    public IReadOnlyDictionary<string, string> Details { get; private set; } = new Dictionary<string, string>();

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public string? CloseReason { get; private set; }

    public string? ClosedBy { get; private set; }

    /// <summary>The reason of a settlement waiting for a second approval (ADR-0037 §7), else null.</summary>
    public string? SettlementReason { get; private set; }

    public string? SettlementComment { get; private set; }

    public string? SettlementProposedBy { get; private set; }

    public DateTimeOffset? SettlementProposedAt { get; private set; }

    /// <summary>When RULE-FO-11 was raised about this gap (settled as unexplained, or open beyond 48 h): at most once.</summary>
    public DateTimeOffset? FraudSuspectedAt { get; private set; }

    /// <summary>Row version (<c>xmin</c>) for <c>If-Match</c>.</summary>
    public uint Version { get; private set; }

    public bool IsOpen => ClosedAt is null;

    public static IntegrityFlag Open(
        TimeProvider clock, TenantId tenant, string rule, string severity, SubjectRef subject, string key, Guid? siteId,
        IReadOnlyList<SubjectRef> related, IReadOnlyList<string> evidence, IReadOnlyDictionary<string, string> details)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(subject);
        var at = clock.GetUtcNow();
        var flag = new IntegrityFlag(EntityId.New<IntegrityFlagId>(clock))
        {
            TenantId = tenant,
            Rule = rule,
            Severity = severity,
            SubjectType = subject.Type,
            SubjectId = subject.Id,
            Key = key,
            SiteId = siteId,
            Related = related,
            Evidence = evidence,
            Details = details,
            OpenedAt = at,
        };
        flag.Raise(new FlagOpened(flag.Id.Value, rule, severity, subject.Type, subject.Id, key, siteId) { OccurredAt = at });
        return flag;
    }

    /// <summary>Closes an open flag with its reason; a closed flag stays as it is.</summary>
    public void Close(TimeProvider clock, string reason, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!IsOpen)
        {
            return;
        }

        var at = clock.GetUtcNow();
        ClosedAt = at;
        CloseReason = reason;
        ClosedBy = actor;
        Raise(new FlagClosed(Id.Value, Rule, reason, actor) { OccurredAt = at });
    }

    /// <summary>
    /// The first person settles an open continuity gap with a reason and what happened; another person approves it
    /// (<see cref="ConfirmSettlement"/>). A data-entry error is corrected instead (ADR-0037 §7).
    /// </summary>
    public Error? ProposeSettlement(TimeProvider clock, string reason, string? comment, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!IsOpen || Rule != ContinuityRule)
        {
            return SettlementErrors.NotSettleable;
        }

        if (!SettlementReasons.All.Contains(reason, StringComparer.Ordinal))
        {
            return SettlementErrors.ReasonInvalid;
        }

        if (reason == SettlementReasons.DataEntryError)
        {
            return SettlementErrors.CorrectTheEntry;
        }

        var said = comment?.Trim();
        if (string.IsNullOrEmpty(said) || said.Length > SettlementErrors.CommentMax)
        {
            return SettlementErrors.CommentRequired;
        }

        if (SettlementReason is not null)
        {
            return SettlementErrors.AlreadyProposed;
        }

        var at = clock.GetUtcNow();
        SettlementReason = reason;
        SettlementComment = said;
        SettlementProposedBy = actor;
        SettlementProposedAt = at;
        Raise(new FlagSettlementProposed(Id.Value, Rule, reason, said, actor) { OccurredAt = at });
        return null;
    }

    /// <summary>Another person than the proposer approves the settlement: the gap closes as settled, its key is never raised again.</summary>
    public Error? ConfirmSettlement(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!IsOpen || SettlementReason is not { } reason)
        {
            return SettlementErrors.NothingProposed;
        }

        if (actor is null || string.Equals(actor, SettlementProposedBy, StringComparison.Ordinal))
        {
            return SettlementErrors.OwnProposal;
        }

        Close(clock, SettlementReasons.ClosePrefix + reason, actor);
        return null;
    }

    /// <summary>The settlement is not approved (or withdrawn by its proposer): the gap stays open, the proposal is gone.</summary>
    public Error? DeclineSettlement(TimeProvider clock, string? comment, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!IsOpen || SettlementReason is not { } reason)
        {
            return SettlementErrors.NothingProposed;
        }

        var said = comment?.Trim();
        if (said is { Length: > SettlementErrors.CommentMax })
        {
            return SettlementErrors.CommentRequired;
        }

        var at = clock.GetUtcNow();
        SettlementReason = null;
        SettlementComment = null;
        SettlementProposedBy = null;
        SettlementProposedAt = null;
        Raise(new FlagSettlementDeclined(Id.Value, Rule, reason, string.IsNullOrEmpty(said) ? null : said, actor) { OccurredAt = at });
        return null;
    }

    /// <summary>
    /// RULE-FO-11 <c>FRAUD_SUSPECTED</c> about this continuity gap (US-P2-FO-08 AC2): on the gap's meter, keyed by the gap,
    /// raised once; null when it was raised already or this is not a continuity gap.
    /// </summary>
    public IntegrityFlag? SuspectFraud(TimeProvider clock, string cause)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Rule != ContinuityRule || FraudSuspectedAt is not null)
        {
            return null;
        }

        FraudSuspectedAt = clock.GetUtcNow();
        return Open(
            clock, TenantId, FraudRule, "critical", new SubjectRef(SubjectType, SubjectId), Id.Value.ToString("D"), SiteId,
            [new SubjectRef("integrity_flag", Id.Value), .. Related], Evidence,
            new Dictionary<string, string>(Details, StringComparer.Ordinal) { ["cause"] = cause, ["gapOpenedAt"] = OpenedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture) });
    }
}
