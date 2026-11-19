using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
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

/// <summary>
/// What a rule found (ADR-0036 §2): opened once per subject, rule and key, closed with a reason, never deleted or
/// reopened (a new finding is a new flag). Severity and the explanation's values are kept as found.
/// </summary>
public sealed class IntegrityFlag : AggregateRoot<IntegrityFlagId>, ITenantOwned
{
    public const string Reevaluated = "re-evaluated";

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
}
