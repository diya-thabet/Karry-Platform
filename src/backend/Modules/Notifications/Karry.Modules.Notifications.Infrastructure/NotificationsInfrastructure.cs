using System.Data.Common;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Notifications.Application;
using Karry.Modules.Notifications.Contracts;
using Karry.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Notifications.Infrastructure;

internal sealed class NotificationStore(NotificationsDbContext db) : INotificationStore
{
    public async Task<IReadOnlySet<Guid>> AlreadyNotifiedAsync(Guid sourceEventId, IReadOnlyCollection<Guid> recipients, CancellationToken cancellationToken)
    {
        var wanted = recipients.ToList();
        return (await db.Notifications.AsNoTracking()
            .Where(n => n.SourceEventId == sourceEventId && wanted.Contains(n.RecipientId))
            .Select(n => n.RecipientId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
    }

    public async Task<IReadOnlyList<Notification>> OfAsync(Guid recipient, bool unreadOnly, int take, CancellationToken cancellationToken)
    {
        var mine = db.Notifications.AsNoTracking().Where(n => n.RecipientId == recipient);
        if (unreadOnly)
        {
            mine = mine.Where(n => n.ReadAt == null);
        }

        return await mine.OrderByDescending(n => n.Id).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<int> UnreadCountAsync(Guid recipient, CancellationToken cancellationToken) =>
        db.Notifications.CountAsync(n => n.RecipientId == recipient && n.ReadAt == null, cancellationToken);

    public Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken) =>
        db.Notifications.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Notification>> UnreadOfAsync(Guid recipient, CancellationToken cancellationToken) =>
        await db.Notifications.Where(n => n.RecipientId == recipient && n.ReadAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(Notification notification) => db.Notifications.Add(notification);

    public async Task<IReadOnlyList<NoticeRuleSetting>> RuleSettingsAsync(CancellationToken cancellationToken) =>
        await db.RuleSettings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<NoticeRuleSetting?> RuleSettingAsync(string type, CancellationToken cancellationToken) =>
        db.RuleSettings.SingleOrDefaultAsync(s => s.Type == type, cancellationToken);

    public void Add(NoticeRuleSetting setting) => db.RuleSettings.Add(setting);

    public async Task<IReadOnlyList<NoticePreference>> PreferencesOfAsync(Guid user, CancellationToken cancellationToken) =>
        await db.Preferences.Where(p => p.UserId == user).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(NoticePreference preference) => db.Preferences.Add(preference);

    public async Task<IReadOnlyList<Notification>> SiteNoticesAsync(
        Guid siteId, IReadOnlyCollection<string> types, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var wanted = types.ToList();
        return await db.Notifications.AsNoTracking()
            .Where(n => n.SiteId == siteId && wanted.Contains(n.Type) && n.CreatedAt >= from && n.CreatedAt < to)
            .OrderBy(n => n.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EmailDelivery>> DueEmailsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        await db.EmailDeliveries
            .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> EmailQueuedAsync(string key, CancellationToken cancellationToken) =>
        db.EmailDeliveries.AnyAsync(d => d.Key == key, cancellationToken);

    public void Add(EmailDelivery delivery) => db.EmailDeliveries.Add(delivery);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>The current company's push subscriptions and pings (RLS), in the notices' unit of work.</summary>
internal sealed class PushStore(NotificationsDbContext db) : IPushStore
{
    public Task<PushSubscription?> SubscriptionAsync(string endpoint, CancellationToken cancellationToken) =>
        db.PushSubscriptions.SingleOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);

    public Task<PushSubscription?> SubscriptionAsync(PushSubscriptionId id, CancellationToken cancellationToken) =>
        db.PushSubscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PushSubscription>> OfPeopleAsync(IReadOnlyCollection<Guid> people, CancellationToken cancellationToken)
    {
        var wanted = people.ToList();
        return await db.PushSubscriptions.AsNoTracking()
            .Where(s => s.Owner == PushOwner.Person && wanted.Contains(s.OwnerId)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PushSubscription>> OfSiteDevicesAsync(Guid siteId, CancellationToken cancellationToken) =>
        await db.PushSubscriptions.AsNoTracking()
            .Where(s => s.Owner == PushOwner.Device && s.SiteId == siteId).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<PushSubscription>> OfDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        await db.PushSubscriptions.Where(s => s.Owner == PushOwner.Device && s.OwnerId == deviceId).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlySet<PushSubscriptionId>> WaitingAsync(IReadOnlyCollection<PushSubscriptionId> subscriptions, CancellationToken cancellationToken)
    {
        var wanted = subscriptions.ToList();
        return (await db.PushPings.AsNoTracking()
            .Where(p => p.Status == DeliveryStatus.Pending && wanted.Contains(p.SubscriptionId))
            .Select(p => p.SubscriptionId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
    }

    public async Task<IReadOnlyList<PushPing>> DuePingsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        await db.PushPings.Where(p => p.Status == DeliveryStatus.Pending && p.NextAttemptAt <= now)
            .OrderBy(p => p.NextAttemptAt).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(PushSubscription subscription) => db.PushSubscriptions.Add(subscription);

    public void Remove(PushSubscription subscription) => db.PushSubscriptions.Remove(subscription);

    public void Add(PushPing ping) => db.PushPings.Add(ping);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// The contract other modules hand notices to (ADR-0039 §1), joined to the transaction of the outbox dispatch that
/// carries the event: the notifications and the event's "processed" marker commit together.
/// </summary>
internal sealed class NoticeInbox(NotificationsDbContext db, NoticeDelivery delivery, ITenantContext tenant) : INotices
{
    public async Task PublishAsync(DbConnection connection, DbTransaction transaction, Notice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var company = tenant.Current ?? throw new InvalidOperationException("Notices are delivered in a tenant.");
        await db.JoinAsync(new Joined(connection, transaction), cancellationToken).ConfigureAwait(false);
        await delivery.DeliverAsync(company, notice, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Joined(DbConnection Connection, DbTransaction Transaction) : ISyncTransaction;
}

public static class NotificationsInfrastructure
{
    /// <summary>Notifications (ADR-0039): their storage, the contract the other modules hand notices to, and the reading.</summary>
    public static IServiceCollection AddNotificationsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<NotificationsDbContext>(connectionString);
        services.AddOutboxDispatcher<NotificationsDbContext>(eventAssemblies: typeof(Notification).Assembly);
        services.AddScoped<INotificationStore, NotificationStore>();
        services.AddScoped<NoticeDelivery>();
        services.AddScoped<INotices, NoticeInbox>();
        services.AddScoped<NotificationReading>();
        services.AddScoped<NoticeSettings>();
        // E-mail (ADR-0039 §4): the records and their sending; the IEmailSender itself is the platform's (Identity's SMTP).
        services.AddScoped<EmailDeliveries>();
        services.AddSingleton<DeliverEmails>();
        // The daily digest at 07:00 site time (ADR-0039 §4); it reads the sites, the review counts and the portal's address.
        services.AddScoped<DailyDigests>();
        services.AddSingleton<QueueDigests>();
        // Web Push (ADR-0039 §5): off without VAPID keys and the company's flag notifications.web-push.
        services.AddOptions<WebPushOptions>().BindConfiguration(WebPushOptions.Section);
        services.TryAddSingleton<IPushSender>(sp => new VapidPushSender(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebPushOptions>>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VapidPushSender>>()));
        services.TryAddScoped<IPushSwitch, PushSwitch>();
        services.AddScoped<IPushStore, PushStore>();
        services.AddScoped<PushSubscriptions>();
        services.AddScoped<PushFanOut>();
        services.AddScoped<PushDeliveries>();
        services.AddSingleton<DeliverPushes>();
        // The tablet's pull (ADR-0039 §3): its site's notices for the people who work on tablets.
        services.AddScoped<ISyncSource, NoticesSync>();
        return services;
    }
}
