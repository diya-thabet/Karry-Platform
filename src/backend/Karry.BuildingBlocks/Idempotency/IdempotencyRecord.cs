namespace Karry.BuildingBlocks.Idempotency;

/// <summary>Stored outcome of a request sent with an <c>Idempotency-Key</c> header.</summary>
public sealed class IdempotencyRecord
{
    /// <summary>Who the key belongs to (tenant and user from P1.5/P1.8; "anonymous" until then).</summary>
    public required string Scope { get; init; }

    public required string Key { get; init; }

    /// <summary>SHA-256 of method, path and body: the same key with a different request is refused.</summary>
    public required string RequestFingerprint { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Null while the first request is still running.</summary>
    public int? StatusCode { get; set; }

    public string? ContentType { get; set; }

    public byte[]? ResponseBody { get; set; }
}
