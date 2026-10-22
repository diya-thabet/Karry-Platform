namespace Karry.SharedKernel.Domain;

/// <summary>
/// Something that happened in the domain. Raised by aggregates, written to the outbox in the same transaction as
/// the change (ADR-0015) and dispatched afterwards. Name events in the past tense (<c>MeterReadingRecorded</c>).
/// </summary>
public abstract record DomainEvent
{
    protected DomainEvent(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        OccurredAt = clock.GetUtcNow();
        EventId = Guid.CreateVersion7(OccurredAt);
    }

    public Guid EventId { get; }

    public DateTimeOffset OccurredAt { get; }
}
