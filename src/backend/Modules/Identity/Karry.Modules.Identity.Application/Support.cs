using System.Diagnostics.Metrics;
using FluentValidation;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Identity.Application;

/// <summary>Support codes and support sessions of the current tenant (ADR-0029 §4).</summary>
public interface ISupportStore
{
    void Add(SupportConsent consent);

    void Add(SupportSession session);

    Task<SupportConsent?> FindConsentAsync(string codeHash, CancellationToken cancellationToken);

    Task<SupportSession?> FindSessionAsync(SupportSessionId id, CancellationToken cancellationToken);

    /// <summary>The newest sessions first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<SupportSession>> ListSessionsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Saves; a concurrent change (the same code used twice at once) is a conflict, not an exception.</summary>
    Task<Result<bool>> SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Tells the company's Tenant Admins that Karry support opened a break-glass session (ADR-0029 §4).</summary>
public interface ISupportNotifier
{
    /// <summary>Number of administrators the e-mail reached (0 when no e-mail provider is configured).</summary>
    Task<int> BreakGlassAsync(BreakGlassNotice notice, CancellationToken cancellationToken);
}

/// <summary>What a Tenant Admin is told of a break-glass session.</summary>
public sealed record BreakGlassNotice(
    IReadOnlyList<(string Name, string Email)> Recipients, string CompanyName, string OperatorName, string Reason, DateTimeOffset StartedAt, DateTimeOffset EndsAt);

/// <summary>The code (shown once) a Tenant Admin reads to Karry support, and until when it can be used.</summary>
public sealed record SupportConsentResponse(Guid ConsentId, string Code, DateTimeOffset ExpiresAt);

/// <summary>Opens a support session: the company's code, or (break-glass) a written reason.</summary>
public sealed record StartSupportSession(string? Code, string? Reason);

/// <summary>The operator who opens a session, as the token names them.</summary>
public sealed record SupportOperator(string Subject, string? Name);

public sealed class StartSupportSessionValidator : AbstractValidator<StartSupportSession>
{
    public StartSupportSessionValidator()
    {
        RuleFor(c => c).Must(c => string.IsNullOrWhiteSpace(c.Code) != string.IsNullOrWhiteSpace(c.Reason))
            .WithErrorCode("support.code_or_reason")
            .WithMessage("Give either the company's support code or a break-glass reason, not both.");
        RuleFor(c => c.Code).MaximumLength(40);
        RuleFor(c => c.Reason).MaximumLength(SupportSession.ReasonMaxLength + 100);
    }
}

/// <summary>Break-glass telemetry: alert <c>KarryBreakGlassSupport</c> fires on every new one.</summary>
public sealed class SupportMetrics
{
    public const string MeterName = "Karry.Identity.Support";

    private readonly Counter<long> breakGlass;

    public SupportMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        breakGlass = meterFactory.Create(MeterName)
            .CreateCounter<long>("karry.support.break_glass.sessions", "{session}", "Break-glass support sessions opened.");
    }

    public void BreakGlassOpened() => breakGlass.Add(1);
}

/// <summary>
/// The company's side of support access (US-P1-OPS-03 AC3), for <c>identity.support.manage</c> (Tenant Admin): create
/// a support code, see past and current sessions, end one now.
/// </summary>
public sealed class SupportAccess(ISupportStore store, ITenantContext tenant, ICallerAccess caller, TimeProvider clock)
{
    public const int ListLimit = 100;

    public static readonly Error NotFound = Error.NotFound("support.session_not_found", "No such support session in this company.");

    public async Task<Result<SupportConsentResponse>> CreateConsentAsync(CancellationToken cancellationToken)
    {
        var current = tenant.Current ?? throw new InvalidOperationException("No tenant.");
        var (consent, code) = SupportConsent.Create(clock, current, caller.Subject);
        store.Add(consent);
        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? new SupportConsentResponse(consent.Id.Value, code, consent.ExpiresAt) : saved.Error;
    }

    public Task<IReadOnlyList<SupportSession>> ListAsync(CancellationToken cancellationToken) => store.ListSessionsAsync(ListLimit, cancellationToken);

    public async Task<Result<SupportSession>> EndAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = id == Guid.Empty ? null : await store.FindSessionAsync(new SupportSessionId(id), cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return NotFound;
        }

        var ended = session.End(clock, caller.Subject, byOperator: false);
        if (!ended.IsSuccess)
        {
            return ended.Error;
        }

        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? session : saved.Error;
    }
}

/// <summary>
/// Karry's side of support access: a platform operator opens a read-only session of one hour on one company, with
/// the company's code or by break-glass, and ends it. Runs under that company's tenant (row-level security, ledger):
/// a code of another company is simply not found.
/// </summary>
public sealed partial class SupportSessions(
    ISupportStore store, ITenantContext tenant, ITenantDirectory tenants, IRoleStore roles, ISupportNotifier notifier,
    SupportMetrics metrics, TimeProvider clock, ILogger<SupportSessions> logger)
{
    public static readonly Error NotFound = Error.NotFound("support.session_not_found", "No such support session for this company.");

    public async Task<Result<SupportSession>> StartAsync(StartSupportSession command, SupportOperator by, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(by);
        var current = tenant.Current ?? throw new InvalidOperationException("No tenant.");
        if (!string.IsNullOrWhiteSpace(command.Code))
        {
            var normalized = EnrollmentCode.Normalize(command.Code);
            var consent = normalized is null ? null : await store.FindConsentAsync(EnrollmentCode.Hash(normalized), cancellationToken).ConfigureAwait(false);
            // A refused code is logged (security review 2026-10-29): an operator trying codes must be visible.
            if (consent is null)
            {
                LogCodeRefused(logger, current.ToString(), by.Subject);
                return SupportConsent.Invalid;
            }

            var used = consent.Use(clock);
            if (!used.IsSuccess)
            {
                LogCodeRefused(logger, current.ToString(), by.Subject);
                return used.Error;
            }

            var session = SupportSession.WithConsent(clock, current, consent, by.Subject, by.Name);
            store.Add(session);
            var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
            return saved.IsSuccess ? session : saved.Error;
        }

        var opened = SupportSession.BreakGlass(clock, current, by.Subject, by.Name, command.Reason);
        if (!opened.IsSuccess)
        {
            return opened.Error;
        }

        store.Add(opened.Value);
        var stored = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        metrics.BreakGlassOpened();
        LogBreakGlass(logger, current.ToString(), opened.Value.Id.Value, by.Subject);
        await NotifyAsync(opened.Value, by, cancellationToken).ConfigureAwait(false);
        return opened.Value;
    }

    public Task<IReadOnlyList<SupportSession>> ListAsync(CancellationToken cancellationToken) => store.ListSessionsAsync(SupportAccess.ListLimit, cancellationToken);

    /// <summary>Ends the operator's own session (another operator's session is not theirs to end).</summary>
    public async Task<Result<SupportSession>> EndAsync(Guid id, SupportOperator by, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(by);
        var session = id == Guid.Empty ? null : await store.FindSessionAsync(new SupportSessionId(id), cancellationToken).ConfigureAwait(false);
        if (session is null || !string.Equals(session.OperatorSubject, by.Subject, StringComparison.Ordinal))
        {
            return NotFound;
        }

        var ended = session.End(clock, by.Subject, byOperator: true);
        if (!ended.IsSuccess)
        {
            return ended.Error;
        }

        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? session : saved.Error;
    }

    // The session is open and ledgered before the e-mail: a mail outage must not block an incident response.
    private async Task NotifyAsync(SupportSession session, SupportOperator by, CancellationToken cancellationToken)
    {
        try
        {
            var company = await tenants.FindAsync(session.TenantId, cancellationToken).ConfigureAwait(false);
            var admins = await roles.AccountHoldersAsync(Roles.TenantAdmin, cancellationToken).ConfigureAwait(false);
            var recipients = admins
                .Where(a => a.Status == UserStatus.Active && !string.IsNullOrWhiteSpace(a.Email))
                .Select(a => (a.DisplayName, a.Email!))
                .ToList();
            var reached = recipients.Count == 0 ? 0 : await notifier.BreakGlassAsync(
                new BreakGlassNotice(recipients, company?.Name ?? string.Empty, by.Name ?? "Karry support", session.Reason ?? string.Empty, session.StartedAt, session.EndsAt),
                cancellationToken).ConfigureAwait(false);
            if (reached == 0)
            {
                LogNotNotified(logger, session.TenantId.ToString(), session.Id.Value);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotifyFailed(logger, ex, session.TenantId.ToString(), session.Id.Value);
        }
    }

    [LoggerMessage(EventId = 2200, Level = LogLevel.Warning, Message = "Break-glass support session {Session} opened on tenant {Tenant} by operator {Operator}")]
    private static partial void LogBreakGlass(ILogger logger, string tenant, Guid session, string @operator);

    [LoggerMessage(EventId = 2206, Level = LogLevel.Warning, Message = "Support code refused on tenant {Tenant} for operator {Operator}")]
    private static partial void LogCodeRefused(ILogger logger, string tenant, string @operator);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Error, Message = "No Tenant Admin of tenant {Tenant} was e-mailed about break-glass session {Session}")]
    private static partial void LogNotNotified(ILogger logger, string tenant, Guid session);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Error, Message = "E-mailing the Tenant Admins of tenant {Tenant} about break-glass session {Session} failed")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception, string tenant, Guid session);
}
