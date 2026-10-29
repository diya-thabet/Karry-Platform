using System.Globalization;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Karry.Modules.Identity.Infrastructure;

internal sealed class SupportStore(IdentityDbContext db) : ISupportStore
{
    public void Add(SupportConsent consent) => db.SupportConsents.Add(consent);

    public void Add(SupportSession session) => db.SupportSessions.Add(session);

    public Task<SupportConsent?> FindConsentAsync(string codeHash, CancellationToken cancellationToken) =>
        db.SupportConsents.SingleOrDefaultAsync(c => c.CodeHash == codeHash, cancellationToken);

    public Task<SupportSession?> FindSessionAsync(SupportSessionId id, CancellationToken cancellationToken) =>
        db.SupportSessions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<SupportSession>> ListSessionsAsync(int limit, CancellationToken cancellationToken) =>
        await db.SupportSessions.AsNoTracking().OrderByDescending(s => s.StartedAt).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<Result<bool>> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Error.Conflict("identity.concurrent_change", "Someone else changed this at the same time. Try again.");
        }
    }
}

/// <summary>
/// E-mails each Tenant Admin when Karry support opens a break-glass session (ADR-0029 §4), in French then English
/// like invitations. One message per administrator, so no one sees the others' addresses.
/// </summary>
internal sealed partial class SupportNotifier(IOptions<MessagingOptions> options, ILogger<SupportNotifier> logger) : ISupportNotifier
{
    public async Task<int> BreakGlassAsync(BreakGlassNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            LogNotConfigured(logger);
            return 0;
        }

        var reached = 0;
        foreach (var (name, email) in notice.Recipients)
        {
            using var mail = new MimeMessage();
            mail.From.Add(MailboxAddress.Parse(settings.From));
            mail.To.Add(new MailboxAddress(name, email));
            mail.Subject = $"Accès support Karry · {notice.CompanyName}";
            mail.Body = new TextPart("plain") { Text = Text(notice, name) };
            try
            {
                await Smtp.SendAsync(settings, mail, cancellationToken).ConfigureAwait(false);
                reached++;
            }
            catch (Exception ex) when (Smtp.IsDeliveryFailure(ex))
            {
                LogDeliveryFailed(logger, ex);
            }
        }

        return reached;
    }

    internal static string Text(BreakGlassNotice notice, string recipient)
    {
        static string At(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return $"""
            Bonjour {recipient},

            {notice.OperatorName}, du support Karry, consulte les données de {notice.CompanyName} sans code de votre part
            (accès d'urgence), en lecture seule, du {At(notice.StartedAt)} au {At(notice.EndsAt)}.
            Motif : {notice.Reason}
            Vous pouvez mettre fin à cet accès dans Karry : Paramètres › Accès support. Chaque consultation est inscrite au journal d'audit.

            ---

            Hello {recipient},

            {notice.OperatorName} of Karry support is looking at {notice.CompanyName}'s data without a code from you
            (emergency access), read only, from {At(notice.StartedAt)} to {At(notice.EndsAt)}.
            Reason: {notice.Reason}
            You can end this access in Karry: Settings › Support access. Everything viewed is recorded in the audit log.
            """;
    }

    [LoggerMessage(EventId = 2203, Level = LogLevel.Warning, Message = "Break-glass notice not e-mailed: no provider configured")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 2204, Level = LogLevel.Error, Message = "Break-glass notice to a Tenant Admin not delivered")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception);
}
