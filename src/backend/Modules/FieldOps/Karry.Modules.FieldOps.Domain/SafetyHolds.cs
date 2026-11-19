using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.FieldOps.Domain;

/// <summary>An answer of a checklist item (US-P2-FO-02 AC2): OK, Not OK or not applicable, with a photo when asked.</summary>
public sealed record ChecklistAnswer(string Item, string Answer, string? Photo);

public static class ChecklistAnswers
{
    public const string Ok = "ok";
    public const string NotOk = "not_ok";
    public const string NotApplicable = "na";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Ok, NotOk, NotApplicable], StringComparer.Ordinal);
}

/// <summary>
/// A signed checklist as received (ADR-0035 §3): one row, never changed. It keeps the template version it answered,
/// the machine, the operator (the envelope's signer) and the answers; <see cref="CriticalItems"/> are the critical
/// items answered Not OK, which opened a hold.
/// </summary>
public sealed class ChecklistSubmission : ITenantOwned
{
    public TenantId TenantId { get; init; }

    public Guid RootId { get; init; }

    public ChecklistTemplateId TemplateId { get; init; }

    public string AssetType { get; init; } = null!;

    public int TemplateVersion { get; init; }

    public Guid AssetId { get; init; }

    public Guid SiteId { get; init; }

    public Guid OperatorId { get; init; }

    public DateTimeOffset SubmittedAt { get; init; }

    public IReadOnlyList<ChecklistAnswer> Answers { get; init; } = [];

    public IReadOnlyList<string> CriticalItems { get; init; } = [];

    public Guid EnvelopeId { get; init; }

    public Guid CommandId { get; init; }

    public Guid DeviceId { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }
}

public readonly record struct SafetyHoldId(Guid Value) : IEntityId<SafetyHoldId>
{
    public static SafetyHoldId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

[EventType("fieldops.asset_marked_unsafe.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record AssetMarkedUnsafe(Guid HoldId, Guid AssetId, Guid SiteId, Guid SubmissionId, IReadOnlyList<string> Items, string? Actor) : DomainEvent;

[EventType("fieldops.safety_hold_released.v1")]
[AuditSubject("asset", nameof(AssetId))]
public sealed record SafetyHoldReleased(Guid HoldId, Guid AssetId, Guid SiteId, string Reason, string? Actor) : DomainEvent;

/// <summary>
/// A machine held as unsafe by a critical Not OK (ADR-0035 §4): opened with the submission, in its transaction, from
/// the time the checklist was signed. Never changed; a <see cref="SafetyHoldRelease"/> ends it.
/// </summary>
public sealed class SafetyHold : AggregateRoot<SafetyHoldId>, ITenantOwned
{
    private SafetyHold(SafetyHoldId id)
        : base(id)
    {
    }

    // EF Core
    private SafetyHold()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public Guid AssetId { get; private set; }

    public Guid SiteId { get; private set; }

    public Guid SubmissionId { get; private set; }

    /// <summary>The critical items answered Not OK.</summary>
    public IReadOnlyList<string> Items { get; private set; } = [];

    /// <summary>When the operator signed the checklist: the machine is held from then on.</summary>
    public DateTimeOffset OpenedAt { get; private set; }

    public Guid OpenedBy { get; private set; }

    public static SafetyHold Open(TimeProvider clock, ChecklistSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(submission);
        var hold = new SafetyHold(EntityId.New<SafetyHoldId>(clock))
        {
            TenantId = submission.TenantId,
            AssetId = submission.AssetId,
            SiteId = submission.SiteId,
            SubmissionId = submission.RootId,
            Items = submission.CriticalItems,
            OpenedAt = submission.SubmittedAt,
            OpenedBy = submission.OperatorId,
        };
        hold.Raise(new AssetMarkedUnsafe(hold.Id.Value, submission.AssetId, submission.SiteId, submission.RootId, submission.CriticalItems, submission.OperatorId.ToString())
        {
            OccurredAt = clock.GetUtcNow(),
        });
        return hold;
    }
}

/// <summary>
/// The decision that ends a hold (ADR-0035 §5): who, when and why, ledgered with the release event. Its ID is the
/// hold's, so a hold is released once.
/// </summary>
public sealed class SafetyHoldRelease : AggregateRoot<SafetyHoldId>, ITenantOwned
{
    public const int ReasonMinLength = 10;
    public const int ReasonMaxLength = 500;

    private SafetyHoldRelease(SafetyHoldId id)
        : base(id)
    {
    }

    // EF Core
    private SafetyHoldRelease()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public Guid AssetId { get; private set; }

    public DateTimeOffset ReleasedAt { get; private set; }

    public string? ReleasedBy { get; private set; }

    public string Reason { get; private set; } = null!;

    public static SafetyHoldRelease Release(TimeProvider clock, SafetyHold hold, string reason, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hold);
        var at = clock.GetUtcNow();
        var release = new SafetyHoldRelease(hold.Id) { TenantId = hold.TenantId, AssetId = hold.AssetId, ReleasedAt = at, ReleasedBy = actor, Reason = reason };
        release.Raise(new SafetyHoldReleased(hold.Id.Value, hold.AssetId, hold.SiteId, reason, actor) { OccurredAt = at });
        return release;
    }
}
