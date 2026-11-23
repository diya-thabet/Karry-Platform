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
public static class ReviewActionKinds
{
    public const string Approve = "approve";
    public const string Reject = "reject";

    public static IReadOnlyList<string> All { get; } = [Approve, Reject];
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

[EventType("verification.review_item_rejected.v1")]
[AuditSubject("review_item", nameof(ItemId), Site = nameof(SiteId))]
public sealed record ReviewItemRejected(
    Guid ItemId, string RecordType, Guid RecordId, int RecordVersion, string RecordHash, Guid SiteId, string ReasonCode, string Comment, string? Actor) : DomainEvent;

/// <summary>
/// One decision on an item, append-only (ADR-0037 §2): who, what, why, and the hash of the record version it was about,
/// so the decision is bound to what the reviewer saw (§3).
/// </summary>
public sealed class ReviewAction : ITenantOwned
{
    public ReviewActionId Id { get; init; }

    public TenantId TenantId { get; init; }

    public ReviewItemId ItemId { get; init; }

    public required string Kind { get; init; }

    public string? Actor { get; init; }

    public string? ReasonCode { get; init; }

    public string? Comment { get; init; }

    public required string RecordHash { get; init; }

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

    public DateTimeOffset CreatedAt { get; private set; }

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
            CreatedAt = at,
        };
        item.Raise(new ReviewItemCreated(item.Id.Value, recordType, recordId, recordVersion, recordHash, siteId) { OccurredAt = at });
        return item;
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

/// <summary>What a decision may not do (ADR-0037 §2, §4).</summary>
public static class ReviewRules
{
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
