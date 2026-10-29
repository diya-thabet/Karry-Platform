using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Domain;

public readonly record struct SupportConsentId(Guid Value) : IEntityId<SupportConsentId>
{
    public static SupportConsentId From(Guid value) => new(value);
}

public readonly record struct SupportSessionId(Guid Value) : IEntityId<SupportSessionId>
{
    public static SupportSessionId From(Guid value) => new(value);
}

/// <summary>How a support session was opened (ADR-0029 §4).</summary>
public enum SupportMode
{
    /// <summary>With a code a Tenant Admin created and read to Karry support.</summary>
    Consent = 1,

    /// <summary>Without the customer (unreachable, production incident), with a written reason; alerts and e-mails.</summary>
    BreakGlass = 2,
}

/// <summary>A Tenant Admin created a support code (US-P1-OPS-03 AC3). The code itself is never recorded.</summary>
[EventType("identity.support_consent_created.v1")]
[AuditSubject("support_consent", nameof(ConsentId))]
public sealed record SupportConsentCreated(Guid ConsentId, DateTimeOffset ExpiresAt, string? Actor) : DomainEvent;

/// <summary>A Karry operator opened a read-only support session on the company, with its code or by break-glass.</summary>
[EventType("identity.support_session_started.v1")]
[AuditSubject("support_session", nameof(SessionId))]
public sealed record SupportSessionStarted(
    Guid SessionId, string Mode, string Operator, string? OperatorName, string? Reason, Guid? ConsentId, DateTimeOffset EndsAt) : DomainEvent;

/// <summary>A support session ended before its hour, by the operator or by someone of the company.</summary>
[EventType("identity.support_session_ended.v1")]
[AuditSubject("support_session", nameof(SessionId))]
public sealed record SupportSessionEnded(Guid SessionId, string? Actor, bool ByOperator) : DomainEvent;

/// <summary>
/// One request Karry support made in a session (ADR-0029 §4): method, route template (never the values in the
/// address) and the status it got, including refusals such as a write (403 <c>support.read_only</c>).
/// </summary>
[EventType("identity.support_request.v1")]
[AuditSubject("support_session", nameof(SessionId))]
public sealed record SupportRequestMade(Guid SessionId, string Method, string Path, int Status) : DomainEvent;

/// <summary>
/// The code a Tenant Admin reads to Karry support so that support may look at the company (ADR-0029 §4): the
/// enrollment-code format (80 bits, 16 characters), only its SHA-256 stored, valid 30 minutes, single use.
/// </summary>
public sealed class SupportConsent : AggregateRoot<SupportConsentId>, ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static readonly Error Invalid = Error.BusinessRule("support.consent_invalid", "This support code is not valid, has expired or was already used. Ask the company for a new one.");

    private SupportConsent(SupportConsentId id, TenantId tenantId, string codeHash, DateTimeOffset createdAt, string? createdBy)
        : base(id)
    {
        TenantId = tenantId;
        CodeHash = codeHash;
        CreatedAt = createdAt;
        ExpiresAt = createdAt + Lifetime;
        CreatedBy = createdBy;
    }

    public TenantId TenantId { get; private set; }

    public string CodeHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public string? CreatedBy { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public static (SupportConsent Consent, string Code) Create(TimeProvider clock, TenantId tenant, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var code = EnrollmentCode.New();
        var consent = new SupportConsent(EntityId.New<SupportConsentId>(clock), tenant, EnrollmentCode.Hash(EnrollmentCode.Normalize(code)!), clock.GetUtcNow(), actor);
        consent.Raise(new SupportConsentCreated(consent.Id.Value, consent.ExpiresAt, actor) { OccurredAt = consent.CreatedAt });
        return (consent, code);
    }

    /// <summary>Uses the code; refused once used or after 30 minutes.</summary>
    public Result<SupportConsent> Use(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        if (UsedAt is not null || now >= ExpiresAt)
        {
            return Invalid;
        }

        UsedAt = now;
        return this;
    }
}

/// <summary>
/// One operator looking at one company, read only, for at most one hour (ADR-0029 §4, US-P1-OPS-03 AC3). Ends at
/// <see cref="EndsAt"/>, or earlier when the operator or a Tenant Admin ends it; it cannot be extended (a new session
/// needs a new code or reason).
/// </summary>
public sealed class SupportSession : AggregateRoot<SupportSessionId>, ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public const int ReasonMinLength = 30;

    public const int ReasonMaxLength = 500;

    public const int OperatorNameMaxLength = 200;

    public static readonly Error ReasonTooShort = Error.Validation(
        "support.reason_required", $"Explain in at least {ReasonMinLength} characters why support must look without the company's code.");

    public static readonly Error AlreadyEnded = Error.BusinessRule("support.session_ended", "This support session has already ended.");

    private SupportSession(
        SupportSessionId id, TenantId tenantId, SupportMode mode, string operatorSubject, string? operatorName, string? reason, SupportConsentId? consentId, DateTimeOffset startedAt)
        : base(id)
    {
        TenantId = tenantId;
        Mode = mode;
        OperatorSubject = operatorSubject;
        OperatorName = operatorName;
        Reason = reason;
        ConsentId = consentId;
        StartedAt = startedAt;
        EndsAt = startedAt + Lifetime;
    }

    public TenantId TenantId { get; private set; }

    public SupportMode Mode { get; private set; }

    /// <summary>The operator's Keycloak subject: only that operator can use the session.</summary>
    public string OperatorSubject { get; private set; }

    public string? OperatorName { get; private set; }

    /// <summary>The written reason of a break-glass session; null with a consent.</summary>
    public string? Reason { get; private set; }

    public SupportConsentId? ConsentId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    /// <summary>When it was ended early; null when it ran (or runs) its full hour.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    public string? EndedBy { get; private set; }

    public bool IsActive(DateTimeOffset now) => EndedAt is null && now < EndsAt;

    /// <summary>When it stopped or will stop: the early end, else the end of its hour.</summary>
    public DateTimeOffset Until => EndedAt ?? EndsAt;

    /// <summary>Opens a session with the company's code (already checked and used).</summary>
    public static SupportSession WithConsent(TimeProvider clock, TenantId tenant, SupportConsent consent, string operatorSubject, string? operatorName)
    {
        ArgumentNullException.ThrowIfNull(consent);
        return Start(clock, tenant, SupportMode.Consent, operatorSubject, operatorName, reason: null, consent.Id);
    }

    /// <summary>Opens a session without the company: refused without a written reason of at least 30 characters.</summary>
    public static Result<SupportSession> BreakGlass(TimeProvider clock, TenantId tenant, string operatorSubject, string? operatorName, string? reason)
    {
        var written = reason?.Trim() ?? string.Empty;
        if (written.Length < ReasonMinLength)
        {
            return ReasonTooShort;
        }

        return Start(clock, tenant, SupportMode.BreakGlass, operatorSubject, operatorName, written.Length > ReasonMaxLength ? written[..ReasonMaxLength] : written, consentId: null);
    }

    /// <summary>Ends it now; refused once ended or past its hour.</summary>
    public Result<SupportSession> End(TimeProvider clock, string? actor, bool byOperator)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        if (!IsActive(now))
        {
            return AlreadyEnded;
        }

        EndedAt = now;
        EndedBy = actor;
        Raise(new SupportSessionEnded(Id.Value, actor, byOperator) { OccurredAt = now });
        return this;
    }

    public const int PathMaxLength = 300;

    /// <summary>
    /// Records a request of the session in the ledger. The session row does not change, so the parallel requests of
    /// one page never conflict with each other.
    /// </summary>
    public void RecordRequest(TimeProvider clock, string method, string path, int status)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        Raise(new SupportRequestMade(Id.Value, method.ToUpperInvariant(), path.Length > PathMaxLength ? path[..PathMaxLength] : path, status)
        { OccurredAt = clock.GetUtcNow() });
    }

    private static SupportSession Start(
        TimeProvider clock, TenantId tenant, SupportMode mode, string operatorSubject, string? operatorName, string? reason, SupportConsentId? consentId)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorSubject);
        var name = string.IsNullOrWhiteSpace(operatorName) ? null : operatorName.Trim();
        if (name is { Length: > OperatorNameMaxLength })
        {
            name = name[..OperatorNameMaxLength];
        }

        var session = new SupportSession(EntityId.New<SupportSessionId>(clock), tenant, mode, operatorSubject, name, reason, consentId, clock.GetUtcNow());
        session.Raise(new SupportSessionStarted(
            session.Id.Value, mode == SupportMode.Consent ? "consent" : "break_glass", operatorSubject, name, reason, consentId?.Value, session.EndsAt)
        { OccurredAt = session.StartedAt });
        return session;
    }
}
