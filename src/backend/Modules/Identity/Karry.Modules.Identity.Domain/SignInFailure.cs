using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Domain;

/// <summary>Keycloak's event ID: the same failure imported twice is the same row.</summary>
public readonly record struct SignInFailureId(Guid Value) : IEntityId<SignInFailureId>
{
    public static SignInFailureId From(Guid value) => new(value);
}

/// <summary>A refused sign-in of a known user, for the tenant's audit ledger (US-P1-IAM-01 AC4). IDs and reason only.</summary>
[EventType("identity.sign_in_failed.v1")]
[AuditSubject("user", nameof(UserId))]
public sealed record SignInFailed(Guid UserId, string Reason, bool Locked) : DomainEvent;

/// <summary>
/// A failed or refused sign-in reported by Keycloak (wrong password, wrong second factor, account locked by the
/// brute-force protection). The client IP is kept here, not in the immutable ledger (personal data must stay erasable).
/// </summary>
public sealed class SignInFailure : AggregateRoot<SignInFailureId>, ITenantOwned
{
    public const int ReasonMaxLength = 40;

    /// <summary>Keycloak error codes Karry records as they are; anything else becomes <c>other</c>.</summary>
    public static readonly IReadOnlySet<string> KnownReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "invalid_user_credentials", "user_temporarily_disabled", "user_disabled", "invalid_authentication_session", "expired_code",
    };

    public const string LockedReason = "user_temporarily_disabled";

    private SignInFailure(SignInFailureId id, TenantId tenantId, UserId userId, string reason, string? ipAddress, DateTimeOffset occurredAt)
        : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        Reason = reason;
        IpAddress = ipAddress;
        OccurredAt = occurredAt;
    }

    public TenantId TenantId { get; private set; }

    public UserId UserId { get; private set; }

    public string Reason { get; private set; }

    public string? IpAddress { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public bool Locked => Reason == LockedReason;

    public static SignInFailure Record(Guid keycloakEventId, UserAccount user, string? keycloakError, string? ipAddress, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(user);
        var reason = keycloakError is not null && KnownReasons.Contains(keycloakError) ? keycloakError : "other";
        var ip = ipAddress is { Length: > 0 and <= 45 } ? ipAddress : null;
        var failure = new SignInFailure(new SignInFailureId(keycloakEventId), user.TenantId, user.Id, reason, ip, occurredAt);
        failure.Raise(new SignInFailed(user.Id.Value, reason, failure.Locked) { EventId = keycloakEventId, OccurredAt = occurredAt });
        return failure;
    }
}
