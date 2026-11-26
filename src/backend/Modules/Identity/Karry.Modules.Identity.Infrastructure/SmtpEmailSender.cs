using Karry.BuildingBlocks.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>
/// The platform's <see cref="IEmailSender"/> (ADR-0039 §4): the SMTP server of <c>Identity:Messaging</c> (Mailpit on the
/// local stack), the same that sends invitations. Identity owns it because it owns the sender settings; any module sends
/// through the port. Logs say what failed, never the address or the text (T-18).
/// </summary>
internal sealed partial class SmtpEmailSender(IOptions<MessagingOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task<EmailOutcome> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            LogNotConfigured(logger);
            return EmailOutcome.NotConfigured;
        }

        using var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(settings.From));
        mail.To.Add(new MailboxAddress(message.ToName ?? string.Empty, message.To));
        mail.Subject = message.Subject;
        mail.Body = new TextPart("plain") { Text = message.Text };

        try
        {
            await Smtp.SendAsync(settings, mail, cancellationToken).ConfigureAwait(false);
            return EmailOutcome.Sent;
        }
        catch (Exception ex) when (Smtp.IsDeliveryFailure(ex))
        {
            LogFailed(logger, ex);
            return EmailOutcome.Failed;
        }
    }

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "E-mail not sent: no SMTP server is configured")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "E-mail not delivered by the SMTP server; it will be tried again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
