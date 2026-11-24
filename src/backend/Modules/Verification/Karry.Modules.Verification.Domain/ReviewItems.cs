using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Verification.Domain;

public readonly record struct ReviewItemId(Guid Value) : IEntityId<ReviewItemId>
{
    /// <summary>UUIDv7 order is creation order: the queue pages by it (oldest first among items of one state).</summary>
    public static bool operator <(ReviewItemId left, ReviewItemId right) => left.Value.CompareTo(right.Value) < 0;

    public static bool operator >(ReviewItemId left, ReviewItemId right) => left.Value.CompareTo(right.Value) > 0;

    public static bool operator <=(ReviewItemId left, ReviewItemId right) => left.Value.CompareTo(right.Value) <= 0;

    public static bool operator >=(ReviewItemId left, ReviewItemId right) => left.Value.CompareTo(right.Value) >= 0;

    public static ReviewItemId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>Where a review item stands (ADR-0037 §2); escalation is a mark on a pending item, not a state.</summary>
public static class ReviewStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Corrected = "corrected";
    public const string AwaitingSecond = "awaiting_second";

    public static IReadOnlyList<string> All { get; } = [Pending, Approved, Rejected, Corrected, AwaitingSecond];
}

public readonly record struct ReviewActionId(Guid Value) : IEntityId<ReviewActionId>
{
    public static ReviewActionId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>What a reviewer did on an item (ADR-0037 §2).</summary>
/// <summary>
/// How urgently an item needs a look (ADR-0038 §1): escalated for waiting past its due time, then flagged by a rule, then
/// the rest. The queue sorts by it, highest first, then oldest first.
/// </summary>
public static class ReviewRisk
{
    public const int None = 0;
    public const int Flagged = 1;
    public const int Overdue = 2;
}

public static class ReviewActionKinds
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Correct = "correct";
    public const string SecondApprove = "second_approve";
    public const string AutoApprove = "auto_approve";
    public const string Escalate = "escalate";

    public static IReadOnlyList<string> All { get; } = [Approve, Reject, Correct, SecondApprove, AutoApprove, Escalate];
}

/// <summary>Why an entry is rejected (US-P2-VER-03 AC1); the comment says the rest.</summary>
public static class RejectionReasons
{
    public const string WrongReading = "wrong_reading";
    public const string PhotoUnreadable = "photo_unreadable";
    public const string WrongMachine = "wrong_machine";
    public const string WrongShift = "wrong_shift";
    public const string Duplicate = "duplicate";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [WrongReading, PhotoUnreadable, WrongMachine, WrongShift, Duplicate, Other];
}

[EventType("verification.review_item_approved.v1")]
[AuditSubject("review_item", nameof(ItemId), Site = nameof(SiteId))]
public sealed record ReviewItemApproved(Guid ItemId, string RecordType, Guid RecordId, int RecordVersion, string RecordHash, Guid SiteId, string? Actor) : DomainEvent;

/// <summary>
/// A correction (US-P2-VER-04 AC2): the version corrected and the new one, both hashes, every field before and after,
/// the reason and the person; the new version is approved at once or waits for a second approver (<c>Material</c>).
/// </summary>
[EventType("verification.review_item_corrected.v1")]
[AuditSubject("review_item", nameof(ItemId), Site = nameof(SiteId))]
public sealed record ReviewItemCorrected(
    Guid ItemId, Guid RecordId, int FromVersion, int ToVersion, string FromHash, string ToHash, IReadOnlyList<CorrectionChange> Changes, string Reason, bool Material,
    Guid SiteId, string? Actor) : DomainEvent;

/// <summary>A field changed by a correction, as the ledger keeps it.</summary>
public sealed record CorrectionChange(string Field, string? Before, string? After);

/// <summary>
/// An item approved by the company's policy (ADR-0037 §5, TC-P2-VER-006): the policy version that decided and the person
/// who published it, so an approval through a policy edit stays visible (threat T-13).
/// </summary>
[EventType("verification.review_item_auto_approved.v1")]
[AuditSubject("review_item", nameof(ItemId), Site = nameof(SiteId))]
public sealed record ReviewItemAutoApproved(Guid ItemId, Guid RecordId, int RecordVersion, string RecordHash, Guid SiteId, int PolicyVersion, string? PolicyPublishedBy)
    : DomainEvent;

/// <summary>
/// An item still waiting after its due time (US-P2-VER-06 AC1, TC-P2-VER-005): escalated once to the policy's role, for
/// the notifications of P2.10 and the console of P2.9.
/// </summary>
[EventType("verification.review_item_escalated.v1")]
[AuditSubject("review_item", nameof(ItemId), Site = nameof(SiteId))]
public sealed record ReviewItemEscalated(Guid ItemId, Guid RecordId, int RecordVersion, Guid SiteId, DateTimeOffset DueAt, string EscalateTo, int PolicyVersion) : DomainEvent;

[EventType("verification.review_item_second_approved.v1")]
[AuditSubject("review_item", nameof(ItemId), Site = nameof(SiteId))]
public sealed record ReviewItemSecondApproved(Guid ItemId, Guid RecordId, int RecordVersion, string RecordHash, Guid SiteId, string? CorrectedBy, string? Actor) : DomainEvent;

[EventType("verification.review_item_rejected.v1")]
[AuditSubject("review_item", nameof(ItemId), Site = nameof(SiteId))]
public sealed record ReviewItemRejected(
    Guid ItemId, string RecordType, Guid RecordId, int RecordVersion, string RecordHash, Guid SiteId, string ReasonCode, string Comment, string? Actor) : DomainEvent;

/// <summary>
/// One decision on an item, append-only (ADR-0037 §2): who, what, why, and the hash of the record version it was about,
/// so the decision is bound to what the reviewer saw (§3).
/// </summary>
public sealed record ReviewAction : ITenantOwned
{
    public ReviewActionId Id { get; init; }

    public TenantId TenantId { get; init; }

    public ReviewItemId ItemId { get; init; }

    public required string Kind { get; init; }

    public string? Actor { get; init; }

    public string? ReasonCode { get; init; }

    public string? Comment { get; init; }

    public required string RecordHash { get; init; }

    /// <summary>The bulk approval this action belongs to (<see cref="ReviewBatch"/>), or null for a single decision.</summary>
    public ReviewBatchId? BatchId { get; init; }

    public DateTimeOffset At { get; init; }
}

[EventType("verification.review_item_created.v1")]
[AuditSubject("review_item", nameof(ItemId), Site = nameof(SiteId))]
public sealed record ReviewItemCreated(Guid ItemId, string RecordType, Guid RecordId, int RecordVersion, string RecordHash, Guid SiteId) : DomainEvent;

/// <summary>
/// One record version waiting for, or carrying, a decision (ADR-0037 §2). It keeps a snapshot of the facts the record
/// type gave when the version was submitted: who produced it, the asset and the time it covers, its flags and the hash
/// of the version as signed, so a decision is bound to exactly what the reviewer saw.
/// </summary>
public sealed class ReviewItem : AggregateRoot<ReviewItemId>, ITenantOwned
{
    private ReviewItem(ReviewItemId id)
        : base(id)
    {
    }

    // EF Core
    private ReviewItem()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public string RecordType { get; private set; } = null!;

    public Guid RecordId { get; private set; }

    public int RecordVersion { get; private set; }

    /// <summary>The version's hash as signed (<c>sha256:</c> of its canonical form); a decision ledgers it.</summary>
    public string RecordHash { get; private set; } = null!;

    public Guid SiteId { get; private set; }

    /// <summary>The person who produced the record (the operator), for separation of duties.</summary>
    public Guid ProducedBy { get; private set; }

    public Guid? AssetId { get; private set; }

    public DateTimeOffset CoveredFrom { get; private set; }

    public DateTimeOffset? CoveredTo { get; private set; }

    /// <summary>The record's flags when it was submitted; bulk approval takes only items without any.</summary>
    public IReadOnlyList<string> Flags { get; private set; } = [];

    public string Status { get; private set; } = ReviewStatuses.Pending;

    /// <summary>For a corrected version's item: the controller who corrected it (never its second approver).</summary>
    public string? CorrectedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The policy version in force when the item was created (0: the defaults), kept whatever is published later (TC-P2-VER-007).</summary>
    public int PolicyVersion { get; private set; }

    /// <summary>When a decision is due (creation plus the policy's SLA); an item still waiting then escalates (slice 6).</summary>
    public DateTimeOffset DueAt { get; private set; }

    /// <summary>When the item was escalated for waiting past <see cref="DueAt"/>, or null; it escalates once.</summary>
    public DateTimeOffset? EscalatedAt { get; private set; }

    /// <summary>The queue's risk order (ADR-0038 §1): <see cref="ReviewRisk"/>, highest first.</summary>
    public int Risk { get; private set; }

    /// <summary>The row version (<c>xmin</c>, H-C1): two reviewers acting at once, one wins (TC-P2-VER-008).</summary>
    public uint Version { get; private set; }

    public static ReviewItem Submit(
        TimeProvider clock, TenantId tenant, string recordType, Guid recordId, int recordVersion, string recordHash, Guid siteId, Guid producedBy,
        Guid? assetId, DateTimeOffset coveredFrom, DateTimeOffset? coveredTo, IReadOnlyList<string> flags)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordHash);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentOutOfRangeException.ThrowIfLessThan(recordVersion, 1);
        var at = clock.GetUtcNow();
        var item = new ReviewItem(EntityId.New<ReviewItemId>(clock))
        {
            TenantId = tenant,
            RecordType = recordType,
            RecordId = recordId,
            RecordVersion = recordVersion,
            RecordHash = recordHash,
            SiteId = siteId,
            ProducedBy = producedBy,
            AssetId = assetId,
            CoveredFrom = coveredFrom,
            CoveredTo = coveredTo,
            Flags = [.. flags],
            Risk = flags.Count > 0 ? ReviewRisk.Flagged : ReviewRisk.None,
            CreatedAt = at,
        };
        item.Raise(new ReviewItemCreated(item.Id.Value, recordType, recordId, recordVersion, recordHash, siteId) { OccurredAt = at });
        return item;
    }

    /// <summary>
    /// Pins the policy in force to a new item (US-P2-VER-05 AC2): its version and the due time; an unflagged item under a
    /// policy that auto-approves is approved by the policy at once (TC-P2-VER-006), and the action is returned.
    /// </summary>
    public ReviewAction? Pin(TimeProvider clock, EffectivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(policy);
        PolicyVersion = policy.Version;
        DueAt = CreatedAt.AddHours(policy.Terms.SlaHours);
        if (!policy.Terms.AutoApprove || Flags.Count > 0 || Status != ReviewStatuses.Pending)
        {
            return null;
        }

        var at = clock.GetUtcNow();
        Status = ReviewStatuses.Approved;
        Raise(new ReviewItemAutoApproved(Id.Value, RecordId, RecordVersion, RecordHash, SiteId, policy.Version, policy.PublishedBy) { OccurredAt = at });
        return Action(clock, ReviewActionKinds.AutoApprove, policy.Actor, null, null, at);
    }

    /// <summary>
    /// Escalates an item still waiting for a decision after its due time (US-P2-VER-06 AC1), once; null when it is not
    /// due, already decided or already escalated.
    /// </summary>
    public ReviewAction? Escalate(TimeProvider clock, DateTimeOffset now, string escalateTo)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (EscalatedAt is not null || now < DueAt || (Status != ReviewStatuses.Pending && Status != ReviewStatuses.AwaitingSecond))
        {
            return null;
        }

        EscalatedAt = now;
        Risk = ReviewRisk.Overdue;
        Raise(new ReviewItemEscalated(Id.Value, RecordId, RecordVersion, SiteId, DueAt, escalateTo, PolicyVersion) { OccurredAt = now });
        return Action(clock, ReviewActionKinds.Escalate, "sla", null, escalateTo, now);
    }

    /// <summary>Approves a pending item (US-P2-VER-02); who may is the caller's business (permissions, separation of duties).</summary>
    public Result<ReviewAction> Approve(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != ReviewStatuses.Pending)
        {
            return ReviewRules.NotPending;
        }

        var at = clock.GetUtcNow();
        Status = ReviewStatuses.Approved;
        Raise(new ReviewItemApproved(Id.Value, RecordType, RecordId, RecordVersion, RecordHash, SiteId, actor) { OccurredAt = at });
        return Action(clock, ReviewActionKinds.Approve, actor, null, null, at);
    }

    /// <summary>
    /// Approves a pending item as part of <paramref name="batch"/> (US-P2-VER-02 AC1, AC2): the batch's single ledger
    /// entry lists the item and its hash, so the item raises no event of its own.
    /// </summary>
    public Result<ReviewAction> ApproveIn(TimeProvider clock, ReviewBatchId batch, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != ReviewStatuses.Pending)
        {
            return ReviewRules.NotPending;
        }

        var at = clock.GetUtcNow();
        Status = ReviewStatuses.Approved;
        return Action(clock, ReviewActionKinds.Approve, actor, null, null, at) with { BatchId = batch };
    }

    /// <summary>
    /// Records a correction of this pending item's version (US-P2-VER-04): this item is <c>corrected</c>; the item of the
    /// new version is returned, approved by the corrector, or awaiting a second approver when the change is material.
    /// </summary>
    public Result<(ReviewAction Action, ReviewItem Next)> Correct(
        TimeProvider clock, string? actor, string? reason, int toVersion, string toHash, IReadOnlyList<CorrectionChange> changes, bool material)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(changes);
        if (Status != ReviewStatuses.Pending)
        {
            return ReviewRules.NotPending;
        }

        var text = reason?.Trim() ?? string.Empty;
        if (text.Length is < ReviewRules.CommentMinLength or > ReviewRules.CommentMaxLength)
        {
            return ReviewRules.CommentInvalid;
        }

        var at = clock.GetUtcNow();
        Status = ReviewStatuses.Corrected;
        Raise(new ReviewItemCorrected(Id.Value, RecordId, RecordVersion, toVersion, RecordHash, toHash, changes, text, material, SiteId, actor) { OccurredAt = at });
        var next = Submit(clock, TenantId, RecordType, RecordId, toVersion, toHash, SiteId, ProducedBy, AssetId, CoveredFrom, CoveredTo, Flags);
        next.CorrectedBy = actor;
        next.PolicyVersion = PolicyVersion;
        next.DueAt = DueAt;
        next.Status = material ? ReviewStatuses.AwaitingSecond : ReviewStatuses.Approved;
        return (Action(clock, ReviewActionKinds.Correct, actor, null, text, at), next);
    }

    /// <summary>The second approval of a material correction (US-P2-VER-04 AC3); never by the person who corrected it.</summary>
    public Result<ReviewAction> SecondApprove(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != ReviewStatuses.AwaitingSecond)
        {
            return ReviewRules.NoSecondApproval;
        }

        if (actor is not null && actor == CorrectedBy)
        {
            return ReviewRules.OwnCorrection;
        }

        var at = clock.GetUtcNow();
        Status = ReviewStatuses.Approved;
        Raise(new ReviewItemSecondApproved(Id.Value, RecordId, RecordVersion, RecordHash, SiteId, CorrectedBy, actor) { OccurredAt = at });
        return Action(clock, ReviewActionKinds.SecondApprove, actor, null, null, at);
    }

    /// <summary>Rejects a pending item with a reason code and a comment (US-P2-VER-03 AC1).</summary>
    public Result<ReviewAction> Reject(TimeProvider clock, string? actor, string? reasonCode, string? comment)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != ReviewStatuses.Pending)
        {
            return ReviewRules.NotPending;
        }

        if (reasonCode is null || !RejectionReasons.All.Contains(reasonCode))
        {
            return ReviewRules.ReasonInvalid;
        }

        var text = comment?.Trim() ?? string.Empty;
        if (text.Length is < ReviewRules.CommentMinLength or > ReviewRules.CommentMaxLength)
        {
            return ReviewRules.CommentInvalid;
        }

        var at = clock.GetUtcNow();
        Status = ReviewStatuses.Rejected;
        Raise(new ReviewItemRejected(Id.Value, RecordType, RecordId, RecordVersion, RecordHash, SiteId, reasonCode, text, actor) { OccurredAt = at });
        return Action(clock, ReviewActionKinds.Reject, actor, reasonCode, text, at);
    }

    private ReviewAction Action(TimeProvider clock, string kind, string? actor, string? reasonCode, string? comment, DateTimeOffset at) => new()
    {
        Id = EntityId.New<ReviewActionId>(clock),
        TenantId = TenantId,
        ItemId = Id,
        Kind = kind,
        Actor = actor,
        ReasonCode = reasonCode,
        Comment = comment,
        RecordHash = RecordHash,
        At = at,
    };
}

public readonly record struct ReviewBatchId(Guid Value) : IEntityId<ReviewBatchId>
{
    public static ReviewBatchId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// A bulk approval's one ledger entry (US-P2-VER-02 AC2): every item approved and the hash of every record version, so a
/// single decision is bound to exactly the versions the reviewer had on screen.
/// </summary>
[EventType("verification.review_items_bulk_approved.v1")]
[AuditSubject("review_batch", nameof(BatchId))]
public sealed record ReviewItemsBulkApproved(Guid BatchId, IReadOnlyList<Guid> ItemIds, IReadOnlyList<string> RecordHashes, string? Actor) : DomainEvent;

/// <summary>One bulk approval (ADR-0037 §2, §3): who, when, which items and the hash of each; append-only.</summary>
public sealed class ReviewBatch : AggregateRoot<ReviewBatchId>, ITenantOwned
{
    private ReviewBatch(ReviewBatchId id)
        : base(id)
    {
    }

    // EF Core
    private ReviewBatch()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public string? Actor { get; private set; }

    public DateTimeOffset At { get; private set; }

    public IReadOnlyList<Guid> ItemIds { get; private set; } = [];

    public IReadOnlyList<string> RecordHashes { get; private set; } = [];

    public static ReviewBatch Approve(TimeProvider clock, TenantId tenant, IReadOnlyList<ReviewItem> items, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(items);
        var at = clock.GetUtcNow();
        var batch = new ReviewBatch(EntityId.New<ReviewBatchId>(clock))
        {
            TenantId = tenant,
            Actor = actor,
            At = at,
            ItemIds = [.. items.Select(i => i.Id.Value)],
            RecordHashes = [.. items.Select(i => i.RecordHash)],
        };
        batch.Raise(new ReviewItemsBulkApproved(batch.Id.Value, batch.ItemIds, batch.RecordHashes, actor) { OccurredAt = at });
        return batch;
    }
}

/// <summary>What a decision may not do (ADR-0037 §2, §4).</summary>
public static class ReviewRules
{
    public static readonly Error NotCorrectable = Error.Validation("verification.not_correctable", "This kind of record is not corrected here.");

    public static readonly Error NoSecondApproval = Error.Conflict("verification.no_second_approval", "This entry is not waiting for a second approval.");

    /// <summary>The person who corrected an entry does not approve their own correction (US-P2-VER-04 AC3, TC-P2-VER-003).</summary>
    public static readonly Error OwnCorrection = Error.Forbidden("verification.sod_own_correction", "Another person approves your correction.");

    /// <summary>How many entries one bulk approval may hold.</summary>
    public const int BulkMax = 100;

    public static readonly Error BulkSize = Error.Validation("verification.bulk_size", $"Approve 1 to {BulkMax} different entries at once.");

    /// <summary>A bulk approval takes entries without flags only (US-P2-VER-02 AC1, TC-P2-VER-002).</summary>
    public static Error BulkFlagged(IEnumerable<Guid> items) =>
        Error.Validation("verification.bulk_flagged", $"Entries with flags are approved one by one: {string.Join(", ", items)}.");

    public const int CommentMinLength = 10;
    public const int CommentMaxLength = 500;

    public static readonly Error NotPending = Error.Conflict("verification.item_decided", "This entry was already decided.");

    public static readonly Error ReasonInvalid = Error.Validation("verification.reason_invalid", "Choose why the entry is rejected.");

    public static readonly Error CommentInvalid =
        Error.Validation("verification.comment_invalid", $"Write {CommentMinLength} to {CommentMaxLength} characters about what is wrong.");

    /// <summary>Approving or rejecting one's own entry (US-P2-VER-02 AC3).</summary>
    public static readonly Error OwnRecord = Error.Forbidden("verification.sod_own_record", "You cannot decide on an entry you made.");

    /// <summary>Deciding on a machine one operated in the time the entry covers (US-P2-VER-02 AC3).</summary>
    public static readonly Error OperatedAsset = Error.Forbidden("verification.sod_operated_asset", "You operated this machine during this entry: another controller decides.");
}
