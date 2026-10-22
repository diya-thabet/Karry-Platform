namespace Karry.SharedKernel.Domain;

/// <summary>
/// Something that happened in the domain. Raised by aggregates, written to the outbox in the same transaction as
/// the change (ADR-0015) and dispatched afterwards. Name events in the past tense and give each a stable,
/// versioned type name with <see cref="EventTypeAttribute"/>:
/// <code>
/// [EventType("assets.meter_reading_recorded.v1")]
/// public sealed record MeterReadingRecorded(Guid MeterId, decimal Hours) : DomainEvent;
/// // raise with the time from the injected clock:
/// Raise(new MeterReadingRecorded(id, hours) { OccurredAt = clock.GetUtcNow() });
/// </code>
/// Events are serialized to JSON: keep them plain data (no services, no entities).
/// </summary>
public abstract record DomainEvent
{
    /// <summary>Unique ID, also the outbox message ID (UUIDv7).</summary>
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    /// <summary>When it happened (from the injected <see cref="TimeProvider"/>), UTC.</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}
