namespace Karry.BuildingBlocks.Idempotency;

/// <summary>Stored outcome of a request sent with an <c>Idempotency-Key</c> header.</summary>
public sealed class IdempotencyRecord
{
    /// <summary>Who the key belongs to: the tenant ("platform" for operator endpoints); the user joins in P1.8.</summary>
    public required string Scope { get; init; }

    public required string Key { get; init; }

    /// <summary>Tenant of the request (RLS column); null for platform-level requests.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>SHA-256 of method, path and body: the same key with a different request is refused.</summary>
    public required string RequestFingerprint { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Null while the first request is still running.</summary>
    public int? StatusCode { get; set; }

    public string? ContentType { get; set; }

    public byte[]? ResponseBody { get; set; }
}
