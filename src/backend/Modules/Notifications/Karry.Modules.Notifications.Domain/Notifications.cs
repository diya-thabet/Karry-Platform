using Karry.SharedKernel.Domain;
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

/// <summary>How a notice reaches a person (ADR-0039 §2): in the apps, in the daily e-mail digest, by Web Push.</summary>
public static class NoticeChannels
{
    /// <summary>The notification centre and the tablet: always on, it is where every notice is kept.</summary>
    public const string InApp = "in_app";

    public const string Digest = "digest";

    public const string Push = "push";

    /// <summary>The channels a person may turn off for themselves; the in-app centre is never off.</summary>
    public static IReadOnlyList<string> Optional { get; } = [Digest, Push];
}

/// <summary>
/// Who hears of a notice type (ADR-0039 §2): the person it names, holders of roles on its site, or across the company;
/// by which channels; and the only parameters its notices may carry (§6): identifiers and codes every reader of the
/// audience may see, never a value, a price, a note or a person's data.
/// </summary>
public sealed record AudienceRule(bool Person, bool NoticeRole, IReadOnlyList<string> SiteRoles, IReadOnlyList<string> CompanyRoles)
{
    public IReadOnlyList<string> Channels { get; init; } = [NoticeChannels.InApp];

    public IReadOnlyList<string> Parameters { get; init; } = [];
}

/// <summary>The default rules of Phase 2 (ADR-0039 §2), reviewed as code; a company may switch one off.</summary>
public static partial class NoticeRules
{
    /// <summary>The longest parameter value kept (an identifier, a code, a timestamp or a short list of codes).</summary>
    public const int MaxParameterLength = 200;

    private static readonly Dictionary<string, AudienceRule> Rules = new(StringComparer.Ordinal)
    {
        ["review.rejected"] = new(Person: true, NoticeRole: false, [], [])
        {
            Channels = [NoticeChannels.InApp, NoticeChannels.Push],
            Parameters = ["reasonCode", "entryId"],
        },
        ["review.escalated"] = new(Person: false, NoticeRole: true, [], [])
        {
            Channels = [NoticeChannels.InApp, NoticeChannels.Digest],
            Parameters = ["dueAt", "entryId"],
        },
        ["asset.unsafe"] = new(Person: false, NoticeRole: false, ["site-manager", "maintenance-planner"], [])
        {
            Channels = [NoticeChannels.InApp, NoticeChannels.Push],
            Parameters = ["assetId", "items"],
        },
        ["integrity.fraud_suspected"] = new(Person: false, NoticeRole: false, [], ["executive", "tenant-admin"])
        {
            Channels = [NoticeChannels.InApp, NoticeChannels.Digest],
            Parameters = ["flagId", "subjectType", "subjectId"],
        },
        ["integrity.missing_end"] = new(Person: false, NoticeRole: false, ["site-manager"], [])
        {
            Channels = [NoticeChannels.InApp, NoticeChannels.Digest],
            Parameters = ["flagId", "subjectType", "subjectId"],
        },
    };

    /// <summary>Every notice type with a rule, in a stable order.</summary>
    public static IReadOnlyList<string> Types { get; } = [.. Rules.Keys];

    /// <summary>The rule of a notice type, or null for a type nobody hears of.</summary>
    public static AudienceRule? For(string type) => Rules.GetValueOrDefault(type);

    /// <summary>
    /// What a notice may carry (ADR-0039 §6, TC-P2-NOT-004): only the parameters its rule names, and only when the
    /// value is an identifier or a code (letters, digits and <c>_ . : , + -</c>, at most 200 characters). Anything
    /// else (a reading, a price, a note, a name) is dropped before the notice is stored, whatever the producer sent.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Mask(AudienceRule rule, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters
            .Where(p => rule.Parameters.Contains(p.Key, StringComparer.Ordinal) && p.Value.Length <= MaxParameterLength && SafeValue().IsMatch(p.Value))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_.:,+\-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SafeValue();
}

public readonly record struct NoticeRuleSettingId(Guid Value) : IEntityId<NoticeRuleSettingId>
{
    public static NoticeRuleSettingId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>A company switched a notice rule on or off (ADR-0039 §2): ledgered, with who did it.</summary>
[EventType("notifications.rule_switched.v1")]
[AuditSubject("notice_rule", nameof(Type))]
public sealed record NoticeRuleSwitched(string Type, bool Enabled, string? Actor) : DomainEvent;

/// <summary>
/// A company's switch of one default rule (ADR-0039 §2): with no row the rule is on. Switching it off stops new notices
/// of that type for everyone in the company; those already delivered stay.
/// </summary>
public sealed class NoticeRuleSetting : AggregateRoot<NoticeRuleSettingId>, ITenantOwned
{
    private NoticeRuleSetting(NoticeRuleSettingId id)
        : base(id)
    {
    }

    // EF Core
    private NoticeRuleSetting()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public string Type { get; private set; } = null!;

    public bool Enabled { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public string? ChangedBy { get; private set; }

    public static NoticeRuleSetting For(TimeProvider clock, TenantId tenant, string type)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new NoticeRuleSetting(EntityId.New<NoticeRuleSettingId>(clock)) { TenantId = tenant, Type = type, Enabled = true, ChangedAt = clock.GetUtcNow() };
    }

    /// <summary>Switches the rule; returns false (and records nothing) when it already is that way.</summary>
    public bool Switch(TimeProvider clock, bool enabled, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Enabled == enabled)
        {
            return false;
        }

        Enabled = enabled;
        ChangedAt = clock.GetUtcNow();
        ChangedBy = actor;
        Raise(new NoticeRuleSwitched(Type, enabled, actor) { OccurredAt = ChangedAt });
        return true;
    }
}

/// <summary>
/// A person turned a channel off (or on again) for one notice type (ADR-0039 §2). Personal, not a business fact: not
/// ledgered. With no row the channel is on. The in-app centre cannot be turned off.
/// </summary>
public sealed class NoticePreference : ITenantOwned
{
    private NoticePreference()
    {
    }

    public TenantId TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string Type { get; private set; } = null!;

    public string Channel { get; private set; } = null!;

    public bool Enabled { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public static NoticePreference For(TimeProvider clock, TenantId tenant, Guid user, string type, string channel, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new NoticePreference { TenantId = tenant, UserId = user, Type = type, Channel = channel, Enabled = enabled, ChangedAt = clock.GetUtcNow() };
    }

    public void Set(TimeProvider clock, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Enabled = enabled;
        ChangedAt = clock.GetUtcNow();
    }
}
