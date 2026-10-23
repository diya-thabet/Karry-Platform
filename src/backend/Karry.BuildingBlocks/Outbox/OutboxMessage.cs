namespace Karry.BuildingBlocks.Outbox;

/// <summary>
/// A domain event waiting to be dispatched, stored in the module's own schema in the same transaction as the
/// business change (ADR-0015). CloudEvents-shaped: <c>id</c>, <c>type</c>, <c>source</c>, <c>time</c>, <c>data</c>.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    /// <summary>Stable event type name (<c>assets.meter_reading_recorded.v1</c>).</summary>
    public required string Type { get; init; }

    /// <summary>Producing module (its schema name).</summary>
    public required string Source { get; init; }

    /// <summary>Tenant of the aggregate that raised the event (null for platform events); subscribers run in its context.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Events with the same key are dispatched strictly in order.</summary>
    public required string AggregateKey { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>JSON of the event (jsonb).</summary>
    public required string Payload { get; init; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Set after the last allowed attempt failed; the message stops blocking its aggregate and raises an alert.</summary>
    public DateTimeOffset? DeadLetteredAt { get; set; }

    /// <summary>Exception type and message of the last failure (no stack trace), for operators.</summary>
    public string? LastError { get; set; }
}

/// <summary>Records that a handler finished a message, so a redelivered message is not handled twice.</summary>
public sealed class ProcessedMessage
{
    public Guid MessageId { get; init; }

    public required string Handler { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}
