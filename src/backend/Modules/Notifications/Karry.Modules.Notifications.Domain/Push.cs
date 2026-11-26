using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Notifications.Domain;

public readonly record struct PushSubscriptionId(Guid Value) : IEntityId<PushSubscriptionId>
{
    public static PushSubscriptionId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>Who a push subscription alerts: a person in their own browser, or a site's tablet (whoever unlocks it).</summary>
public enum PushOwner
{
    Person = 1,
    Device = 2,
}

/// <summary>
/// A browser's or tablet's Web Push subscription (ADR-0039 §5): the push service's endpoint, which is a capability (never
/// logged, never shown), and who it alerts. A person subscribes their own browser; a tablet subscribes itself, for its
/// site (it holds the site's rejections and shows each person only theirs). The endpoint is one subscription.
/// </summary>
public sealed class PushSubscription : ITenantOwned
{
    private PushSubscription()
    {
    }

    public PushSubscriptionId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public PushOwner Owner { get; private set; }

    /// <summary>The person (an Identity user) or the device (an enrolled tablet).</summary>
    public Guid OwnerId { get; private set; }

    /// <summary>A tablet's site; null for a person.</summary>
    public Guid? SiteId { get; private set; }

    public string Endpoint { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public static PushSubscription For(TimeProvider clock, TenantId tenant, PushOwner owner, Guid ownerId, Guid? siteId, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new PushSubscription
        {
            Id = EntityId.New<PushSubscriptionId>(clock),
            TenantId = tenant,
            Owner = owner,
            OwnerId = ownerId,
            SiteId = siteId,
            Endpoint = endpoint,
            CreatedAt = clock.GetUtcNow(),
        };
    }
}

public readonly record struct PushPingId(Guid Value) : IEntityId<PushPingId>
{
    public static PushPingId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// One push to one subscription (ADR-0039 §5): it carries nothing, the device only says that something needs a look and
/// opens the app (a lock screen is public). At most one waits per subscription, however many notices come; a failure is
/// tried again after 1, 5 and 15 minutes, then given up (the notice itself is in the app anyway).
/// </summary>
public sealed class PushPing : ITenantOwned
{
    public static readonly IReadOnlyList<TimeSpan> Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    public const int MaxAttempts = 4;

    private PushPing()
    {
    }

    public PushPingId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public PushSubscriptionId SubscriptionId { get; private set; }

    public DeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>Why the last attempt failed, as a code (<c>push_failed</c>, <c>not_configured</c>), never a message.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static PushPing Queue(TimeProvider clock, TenantId tenant, PushSubscriptionId subscription)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        return new PushPing
        {
            Id = EntityId.New<PushPingId>(clock),
            TenantId = tenant,
            SubscriptionId = subscription,
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };
    }

    public void Sent()
    {
        Attempts++;
        Status = DeliveryStatus.Sent;
        LastError = null;
    }

    /// <summary>Gives up at once: the subscription is gone (unsubscribed, or the app removed), so no retry can reach it.</summary>
    public void GiveUp(string reason)
    {
        Attempts++;
        LastError = reason;
        Status = DeliveryStatus.Failed;
    }

    public void Failed(TimeProvider clock, string reason)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Attempts++;
        LastError = reason;
        if (Attempts >= MaxAttempts)
        {
            Status = DeliveryStatus.Failed;
            return;
        }

        NextAttemptAt = clock.GetUtcNow() + Backoff[Math.Min(Attempts - 1, Backoff.Count - 1)];
    }
}
