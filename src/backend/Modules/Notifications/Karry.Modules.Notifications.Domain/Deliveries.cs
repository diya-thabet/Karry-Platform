using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Notifications.Domain;

public readonly record struct EmailDeliveryId(Guid Value) : IEntityId<EmailDeliveryId>
{
    public static EmailDeliveryId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>Where an e-mail or a push stands: waiting (first try or a retry), sent, or given up.</summary>
public enum DeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
}

/// <summary>
/// One e-mail to one person and its attempts (ADR-0039 §4): a failure is tried again with backoff and never blocks the
/// in-app notice. It keeps the person (an Identity user), not the address: the address is looked up at each attempt, so
/// a person who left or changed address is not mailed at the old one, and no record or log holds it (T-18). The text is
/// counts and links only, never an entry's values. Deleted after 90 days with the notices.
/// </summary>
public sealed class EmailDelivery : ITenantOwned
{
    /// <summary>Waits before each retry: 1, 5, 15, 60, 240 minutes; after the sixth failed attempt it is given up.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4)];

    public const int MaxAttempts = 6;

    private EmailDelivery()
    {
    }

    public EmailDeliveryId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid RecipientId { get; private set; }

    /// <summary>What the e-mail is (<c>digest</c>).</summary>
    public string Kind { get; private set; } = null!;

    /// <summary>Unique per company: the same e-mail is never queued twice (a digest: person, site and day).</summary>
    public string Key { get; private set; } = null!;

    public string Subject { get; private set; } = null!;

    public string Text { get; private set; } = null!;

    public DeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>Why the last attempt failed, as a code (<c>smtp_failed</c>, <c>not_configured</c>, <c>no_address</c>), never a message.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public static EmailDelivery Queue(TimeProvider clock, TenantId tenant, Guid recipient, string kind, string key, string subject, string text)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        return new EmailDelivery
        {
            Id = EntityId.New<EmailDeliveryId>(clock),
            TenantId = tenant,
            RecipientId = recipient,
            Kind = kind,
            Key = key,
            Subject = subject,
            Text = text,
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };
    }

    public void Sent(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Attempts++;
        Status = DeliveryStatus.Sent;
        SentAt = clock.GetUtcNow();
        LastError = null;
    }

    /// <summary>
    /// An attempt failed: tried again after the backoff, or given up after <see cref="MaxAttempts"/>; a person without an
    /// address (<paramref name="final"/>) is given up at once.
    /// </summary>
    public void Failed(TimeProvider clock, string reason, bool final = false)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Attempts++;
        LastError = reason;
        if (final || Attempts >= MaxAttempts)
        {
            Status = DeliveryStatus.Failed;
            return;
        }

        NextAttemptAt = clock.GetUtcNow() + Backoff[Math.Min(Attempts - 1, Backoff.Count - 1)];
    }
}
