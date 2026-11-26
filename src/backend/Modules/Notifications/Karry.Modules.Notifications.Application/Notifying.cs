using Karry.Modules.Identity.Contracts;
using Karry.Modules.Notifications.Contracts;
using Karry.Modules.Notifications.Domain;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Notifications.Application;

/// <summary>The current company's notifications (RLS).</summary>
public interface INotificationStore
{
    /// <summary>The people among <paramref name="recipients"/> who already have the notice of <paramref name="sourceEventId"/>.</summary>
    Task<IReadOnlySet<Guid>> AlreadyNotifiedAsync(Guid sourceEventId, IReadOnlyCollection<Guid> recipients, CancellationToken cancellationToken);

    /// <summary>A person's notifications, newest first; only the unread ones when asked.</summary>
    Task<IReadOnlyList<Notification>> OfAsync(Guid recipient, bool unreadOnly, int take, CancellationToken cancellationToken);

    Task<int> UnreadCountAsync(Guid recipient, CancellationToken cancellationToken);

    Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken);

    /// <summary>A person's unread notifications, to mark them read.</summary>
    Task<IReadOnlyList<Notification>> UnreadOfAsync(Guid recipient, CancellationToken cancellationToken);

    void Add(Notification notification);

    /// <summary>The company's switched rules (a type with no row is on).</summary>
    Task<IReadOnlyList<NoticeRuleSetting>> RuleSettingsAsync(CancellationToken cancellationToken);

    Task<NoticeRuleSetting?> RuleSettingAsync(string type, CancellationToken cancellationToken);

    void Add(NoticeRuleSetting setting);

    /// <summary>A person's channel choices (a type and channel with no row is on).</summary>
    Task<IReadOnlyList<NoticePreference>> PreferencesOfAsync(Guid user, CancellationToken cancellationToken);

    void Add(NoticePreference preference);

    /// <summary>E-mails waiting whose next attempt is due at <paramref name="now"/>, oldest first.</summary>
    Task<IReadOnlyList<EmailDelivery>> DueEmailsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>Whether an e-mail with this key was already queued (a digest is queued once per person, site and day).</summary>
    Task<bool> EmailQueuedAsync(string key, CancellationToken cancellationToken);

    void Add(EmailDelivery delivery);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Who receives a notice and what they get (ADR-0039 §1, §2): the rule of its type gives the audience, resolved now
/// through Identity; each person gets one notification per event, however often the event is delivered.
/// </summary>
public sealed class NoticeDelivery(INotificationStore store, INoticeAudiences audiences, TimeProvider clock)
{
    /// <summary>Stores the notice's notifications; returns how many people it reached for the first time.</summary>
    public async Task<int> DeliverAsync(TenantId tenant, Notice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (NoticeRules.For(notice.Type) is not { } rule)
        {
            return 0;
        }

        // A company may switch a default rule off (ADR-0039 §2): no new notice of that type for anyone.
        if (await store.RuleSettingAsync(notice.Type, cancellationToken).ConfigureAwait(false) is { Enabled: false })
        {
            return 0;
        }

        // Only what every reader may see is kept (ADR-0039 §6, TC-P2-NOT-004), whatever the producer sent.
        var parameters = NoticeRules.Mask(rule, notice.Parameters);

        var recipients = new HashSet<Guid>();
        if (rule.Person && notice.Person is { } person)
        {
            recipients.Add(person);
        }

        if (rule.NoticeRole && notice.Role is { } role && notice.SiteId is { } roleSite)
        {
            recipients.UnionWith(await audiences.HoldersAsync([role], roleSite, cancellationToken).ConfigureAwait(false));
        }

        if (rule.SiteRoles.Count > 0 && notice.SiteId is { } site)
        {
            recipients.UnionWith(await audiences.HoldersAsync(rule.SiteRoles, site, cancellationToken).ConfigureAwait(false));
        }

        if (rule.CompanyRoles.Count > 0)
        {
            recipients.UnionWith(await audiences.HoldersAsync(rule.CompanyRoles, null, cancellationToken).ConfigureAwait(false));
        }

        if (recipients.Count == 0)
        {
            return 0;
        }

        var already = await store.AlreadyNotifiedAsync(notice.SourceEventId, recipients, cancellationToken).ConfigureAwait(false);
        var reached = 0;
        foreach (var recipient in recipients.Where(r => !already.Contains(r)))
        {
            store.Add(Notification.For(clock, tenant, recipient, notice.Type, parameters, notice.Link, notice.SiteId, notice.SourceEventId));
            reached++;
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return reached;
    }
}

/// <summary>A page of a person's notifications, newest first, and how many are unread in all.</summary>
public sealed record NotificationPage(IReadOnlyList<Notification> Items, int Unread);

/// <summary>A person's own notifications (ADR-0039 §3): read, mark read, mark all read. Nobody reads another's.</summary>
public sealed class NotificationReading(INotificationStore store, INoticeAudiences audiences, Karry.BuildingBlocks.Authorization.ICallerAccess caller, TimeProvider clock)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static readonly Error LimitInvalid = Error.Validation("notifications.limit_invalid", $"A page holds 1 to {MaxLimit} notifications.");

    public static readonly Error NotFound = Error.NotFound("notifications.not_found", "No such notification.");

    public async Task<Result<NotificationPage>> MineAsync(bool unreadOnly, int? limit, CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return LimitInvalid;
        }

        if (await MeAsync(cancellationToken).ConfigureAwait(false) is not { } me)
        {
            return new NotificationPage([], 0);
        }

        return new NotificationPage(
            await store.OfAsync(me, unreadOnly, take, cancellationToken).ConfigureAwait(false),
            await store.UnreadCountAsync(me, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<Notification>> MarkReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var me = await MeAsync(cancellationToken).ConfigureAwait(false);
        var notification = await store.FindAsync(new NotificationId(id), cancellationToken).ConfigureAwait(false);
        if (notification is null || me is null || notification.RecipientId != me)
        {
            return NotFound;
        }

        notification.MarkRead(clock);
        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return notification;
    }

    public async Task<int> MarkAllReadAsync(CancellationToken cancellationToken)
    {
        if (await MeAsync(cancellationToken).ConfigureAwait(false) is not { } me)
        {
            return 0;
        }

        var unread = await store.UnreadOfAsync(me, cancellationToken).ConfigureAwait(false);
        foreach (var notification in unread)
        {
            notification.MarkRead(clock);
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return unread.Count;
    }

    /// <summary>The signed-in person as an Identity user; null for an account with no user in this company.</summary>
    private Task<Guid?> MeAsync(CancellationToken cancellationToken) =>
        caller.Subject is { } subject ? audiences.UserIdOfAsync(subject, cancellationToken) : Task.FromResult<Guid?>(null);
}
