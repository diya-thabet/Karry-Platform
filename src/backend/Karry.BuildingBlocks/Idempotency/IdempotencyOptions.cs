namespace Karry.BuildingBlocks.Idempotency;

public sealed record IdempotencyOptions
{
    public const string HeaderName = "Idempotency-Key";

    public const string ReplayedHeaderName = "Idempotency-Replayed";

    public const int MaxKeyLength = 200;

    /// <summary>How long a completed response can be replayed.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// After this time an unfinished first request is considered abandoned (process crash) and the key may be retried.
    /// Requests must finish well within it; domain-level command IDs protect the critical field-sync path (P2).
    /// </summary>
    public TimeSpan InProgressTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
