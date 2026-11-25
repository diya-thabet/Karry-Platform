using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Notifications.Domain;

public readonly record struct NotificationId(Guid Value) : IEntityId<NotificationId>
{
    /// <summary>UUIDv7 order is creation order: the list pages by it, newest first.</summary>
    public static bool operator <(NotificationId left, NotificationId right) => left.Value.CompareTo(right.Value) < 0;

    public static bool operator >(NotificationId left, NotificationId right) => left.Value.CompareTo(right.Value) > 0;

    public static bool operator <=(NotificationId left, NotificationId right) => left.Value.CompareTo(right.Value) <= 0;

    public static bool operator >=(NotificationId left, NotificationId right) => left.Value.CompareTo(right.Value) >= 0;

    public static NotificationId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// One notice for one person (ADR-0039 §1): its type, the values its words name, where it opens, when it came and
/// when the person read it. Not a business fact: not ledgered, deleted after 90 days.
/// </summary>
public sealed class Notification : ITenantOwned
{
    private Notification()
    {
    }

    public NotificationId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    /// <summary>The person (an Identity user) the notice is for.</summary>
    public Guid RecipientId { get; private set; }

    public string Type { get; private set; } = null!;

    public IReadOnlyDictionary<string, string> Parameters { get; private set; } = new Dictionary<string, string>();

    public string Link { get; private set; } = null!;

    public Guid? SiteId { get; private set; }

    /// <summary>The domain event the notice comes from: one notice per event and person.</summary>
    public Guid SourceEventId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public static Notification For(
        TimeProvider clock, TenantId tenant, Guid recipient, string type, IReadOnlyDictionary<string, string> parameters, string link, Guid? siteId, Guid sourceEventId)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new Notification
        {
            Id = EntityId.New<NotificationId>(clock),
            TenantId = tenant,
            RecipientId = recipient,
            Type = type,
            Parameters = parameters,
            Link = link,
            SiteId = siteId,
            SourceEventId = sourceEventId,
            CreatedAt = clock.GetUtcNow(),
        };
    }

    /// <summary>Marks the notice read once; reading it again changes nothing.</summary>
    public void MarkRead(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ReadAt ??= clock.GetUtcNow();
    }
}

/// <summary>Who hears of a notice type (ADR-0039 §2): the person it names, holders of roles on its site, or across the company.</summary>
public sealed record AudienceRule(bool Person, bool NoticeRole, IReadOnlyList<string> SiteRoles, IReadOnlyList<string> CompanyRoles);

/// <summary>The default rules of Phase 2 (ADR-0039 §2), reviewed as code.</summary>
public static class NoticeRules
{
    private static readonly Dictionary<string, AudienceRule> Rules = new(StringComparer.Ordinal)
    {
        ["review.rejected"] = new(Person: true, NoticeRole: false, [], []),
        ["review.escalated"] = new(Person: false, NoticeRole: true, [], []),
        ["asset.unsafe"] = new(Person: false, NoticeRole: false, ["site-manager", "maintenance-planner"], []),
        ["integrity.fraud_suspected"] = new(Person: false, NoticeRole: false, [], ["executive", "tenant-admin"]),
        ["integrity.missing_end"] = new(Person: false, NoticeRole: false, ["site-manager"], []),
    };

    /// <summary>The rule of a notice type, or null for a type nobody hears of.</summary>
    public static AudienceRule? For(string type) => Rules.GetValueOrDefault(type);
}
