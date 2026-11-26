using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Notifications.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Notifications.Application;

/// <summary>What became of a push.</summary>
public enum PushOutcome
{
    /// <summary>The push service took it.</summary>
    Sent,

    /// <summary>The subscription no longer exists (the browser unsubscribed or the app was removed): it is deleted.</summary>
    Gone,

    /// <summary>The push service could not be reached or refused it for now: worth trying again.</summary>
    Failed,

    /// <summary>No VAPID keys are configured: nothing was tried.</summary>
    NotConfigured,
}

/// <summary>
/// Sends an empty Web Push to a subscription's endpoint (ADR-0039 §5): no payload, so nothing to encrypt and nothing on a
/// lock screen; the app opens and reads what needs a look behind the sign-in. Never throws for a delivery failure.
/// </summary>
public interface IPushSender
{
    /// <summary>The VAPID public key the browsers subscribe with (base64url), or null when push is not configured.</summary>
    string? PublicKey { get; }

    Task<PushOutcome> PingAsync(string endpoint, CancellationToken cancellationToken);
}

/// <summary>Whether Web Push is on for the current company: configured, and its flag <c>notifications.web-push</c> on.</summary>
public interface IPushSwitch
{
    Task<bool> OnAsync(CancellationToken cancellationToken);
}

/// <summary>The current company's push subscriptions and pings (RLS).</summary>
public interface IPushStore
{
    Task<PushSubscription?> SubscriptionAsync(string endpoint, CancellationToken cancellationToken);

    Task<PushSubscription?> SubscriptionAsync(PushSubscriptionId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PushSubscription>> OfPeopleAsync(IReadOnlyCollection<Guid> people, CancellationToken cancellationToken);

    Task<IReadOnlyList<PushSubscription>> OfSiteDevicesAsync(Guid siteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PushSubscription>> OfDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>The subscriptions among <paramref name="subscriptions"/> that already have a ping waiting.</summary>
    Task<IReadOnlySet<PushSubscriptionId>> WaitingAsync(IReadOnlyCollection<PushSubscriptionId> subscriptions, CancellationToken cancellationToken);

    Task<IReadOnlyList<PushPing>> DuePingsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);

    void Add(PushSubscription subscription);

    void Remove(PushSubscription subscription);

    void Add(PushPing ping);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Which push services a subscription may point to: the browsers' own, over HTTPS (a client cannot make the server call elsewhere).</summary>
public static class PushEndpoints
{
    public const int MaxLength = 1000;

    /// <summary>The hosts of the push services of Chrome and Edge (FCM), Firefox, Edge on Windows and Safari.</summary>
    public static IReadOnlyList<string> Hosts { get; } =
        ["fcm.googleapis.com", "updates.push.services.mozilla.com", "push.services.mozilla.com", "notify.windows.com", "push.apple.com"];

    public static readonly Error Invalid = Error.Validation(
        "notifications.push_endpoint_invalid", "A push subscription must be an HTTPS address of a browser's push service.");

    public static bool IsAllowed(string? endpoint) =>
        endpoint is { Length: > 0 and <= MaxLength }
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && Hosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Push subscriptions (ADR-0039 §5): a person subscribes their own browser, a tablet itself for its site. Only while push
/// is on for the company; an endpoint is one subscription (a browser that changed hands follows its new person).
/// </summary>
public sealed class PushSubscriptions(IPushStore store, IPushSender sender, IPushSwitch pushSwitch, INoticeAudiences audiences, ITenantContext tenant, ICallerAccess caller, TimeProvider clock)
{
    public static readonly Error Off = Error.NotFound("notifications.push_off", "Push alerts are not on for this company.");

    public static readonly Error NoAccount = Error.Forbidden("notifications.no_account", "Only a person of this company subscribes to push alerts.");

    /// <summary>The key a browser subscribes with, while push is on.</summary>
    public async Task<Result<string>> KeyAsync(CancellationToken cancellationToken) =>
        sender.PublicKey is { } key && await pushSwitch.OnAsync(cancellationToken).ConfigureAwait(false) ? key : Off;

    public async Task<Result<bool>> SubscribeMineAsync(string? endpoint, CancellationToken cancellationToken)
    {
        if (await MeAsync(cancellationToken).ConfigureAwait(false) is not { } me)
        {
            return NoAccount;
        }

        return await SubscribeAsync(PushOwner.Person, me, null, endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the caller's subscription of this endpoint; another person's is left alone.</summary>
    public async Task<Result<bool>> UnsubscribeMineAsync(string? endpoint, CancellationToken cancellationToken)
    {
        if (await MeAsync(cancellationToken).ConfigureAwait(false) is not { } me)
        {
            return NoAccount;
        }

        if (endpoint is not null && await store.SubscriptionAsync(endpoint, cancellationToken).ConfigureAwait(false) is { Owner: PushOwner.Person } mine && mine.OwnerId == me)
        {
            store.Remove(mine);
            await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public Task<Result<bool>> SubscribeDeviceAsync(DeviceCaller device, string? endpoint, CancellationToken cancellationToken) =>
        SubscribeAsync(PushOwner.Device, device.DeviceId, device.SiteId, endpoint, cancellationToken);

    /// <summary>Removes every subscription of the calling tablet (it has one browser).</summary>
    public async Task UnsubscribeDeviceAsync(DeviceCaller device, CancellationToken cancellationToken)
    {
        foreach (var subscription in await store.OfDeviceAsync(device.DeviceId, cancellationToken).ConfigureAwait(false))
        {
            store.Remove(subscription);
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<bool>> SubscribeAsync(PushOwner owner, Guid ownerId, Guid? siteId, string? endpoint, CancellationToken cancellationToken)
    {
        if (!PushEndpoints.IsAllowed(endpoint))
        {
            return PushEndpoints.Invalid;
        }

        if (!await pushSwitch.OnAsync(cancellationToken).ConfigureAwait(false))
        {
            return Off;
        }

        var company = tenant.Current ?? throw new InvalidOperationException("Subscriptions belong to a tenant.");
        var existing = await store.SubscriptionAsync(endpoint!, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Owner == owner && existing.OwnerId == ownerId && existing.SiteId == siteId)
        {
            return true;
        }

        if (existing is not null)
        {
            store.Remove(existing);
            await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        store.Add(PushSubscription.For(clock, company, owner, ownerId, siteId, endpoint!));
        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task<Guid?> MeAsync(CancellationToken cancellationToken) =>
        caller.Subject is { } subject ? audiences.UserIdOfAsync(subject, cancellationToken) : Task.FromResult<Guid?>(null);
}

/// <summary>
/// Who a stored notice alerts by push (ADR-0039 §5), queued in the notice's transaction: a tablet notice (a rejection)
/// alerts the site's tablets unless its person turned push off; another alerts the browsers of its recipients who kept
/// push. One ping waits per subscription at most.
/// </summary>
public sealed class PushFanOut(IPushStore pushes, INotificationStore notices, IPushSwitch pushSwitch, TimeProvider clock)
{
    public async Task<int> QueueAsync(TenantId tenant, AudienceRule rule, string type, Guid? siteId, Guid? person, IReadOnlyCollection<Guid> recipients, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(recipients);
        if (!rule.Channels.Contains(NoticeChannels.Push) || recipients.Count == 0 || !await pushSwitch.OnAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        IReadOnlyList<PushSubscription> targets;
        if (rule.Tablet)
        {
            var off = person is { } who && await PushOffAsync(who, type, cancellationToken).ConfigureAwait(false);
            targets = off || siteId is not { } site ? [] : await pushes.OfSiteDevicesAsync(site, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var kept = new List<Guid>();
            foreach (var recipient in recipients)
            {
                if (!await PushOffAsync(recipient, type, cancellationToken).ConfigureAwait(false))
                {
                    kept.Add(recipient);
                }
            }

            targets = kept.Count == 0 ? [] : await pushes.OfPeopleAsync(kept, cancellationToken).ConfigureAwait(false);
        }

        if (targets.Count == 0)
        {
            return 0;
        }

        var waiting = await pushes.WaitingAsync([.. targets.Select(t => t.Id)], cancellationToken).ConfigureAwait(false);
        var queued = 0;
        foreach (var target in targets.Where(t => !waiting.Contains(t.Id)))
        {
            pushes.Add(PushPing.Queue(clock, tenant, target.Id));
            queued++;
        }

        return queued;
    }

    private async Task<bool> PushOffAsync(Guid person, string type, CancellationToken cancellationToken) =>
        (await notices.PreferencesOfAsync(person, cancellationToken).ConfigureAwait(false))
        .Any(p => p.Type == type && p.Channel == NoticeChannels.Push && !p.Enabled);
}

/// <summary>Sends the current company's due pings; a subscription that is gone is deleted (ADR-0039 §5).</summary>
public sealed class PushDeliveries(IPushStore store, IPushSender sender, TimeProvider clock)
{
    public const int BatchSize = 100;

    public async Task<int> SendDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var ping in await store.DuePingsAsync(now, BatchSize, cancellationToken).ConfigureAwait(false))
        {
            if (await store.SubscriptionAsync(ping.SubscriptionId, cancellationToken).ConfigureAwait(false) is not { } subscription)
            {
                ping.GiveUp("gone");
            }
            else
            {
                switch (await sender.PingAsync(subscription.Endpoint, cancellationToken).ConfigureAwait(false))
                {
                    case PushOutcome.Sent:
                        ping.Sent();
                        sent++;
                        break;
                    case PushOutcome.Gone:
                        store.Remove(subscription);
                        ping.GiveUp("gone");
                        break;
                    case PushOutcome.NotConfigured:
                        ping.Failed(clock, "not_configured");
                        break;
                    default:
                        ping.Failed(clock, "push_failed");
                        break;
                }
            }

            await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return sent;
    }
}

/// <summary>What a run of the push deliveries did.</summary>
public sealed record PushRun(int Sent, int Failed);

/// <summary>Sends the due pings company by company (every minute); one company's failure does not stop the others.</summary>
public sealed partial class DeliverPushes(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DeliverPushes> logger)
{
    public Task<PushRun> RunAsync(CancellationToken cancellationToken) => RunAtAsync(clock.GetUtcNow(), cancellationToken);

    public async Task<PushRun> RunAtAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> companies;
        await using (var scope = scopes.CreateAsyncScope())
        {
            companies = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var sent = 0;
        var failed = 0;
        foreach (var company in companies)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(company);
                sent += await scope.ServiceProvider.GetRequiredService<PushDeliveries>().SendDueAsync(now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogFailed(logger, ex, company.Value);
            }
        }

        return new PushRun(sent, failed);
    }

    [LoggerMessage(EventId = 7603, Level = LogLevel.Error, Message = "Sending the due pushes of tenant {Tenant} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenant);
}
