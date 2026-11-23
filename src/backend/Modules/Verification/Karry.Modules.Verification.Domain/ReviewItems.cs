using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
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
}
