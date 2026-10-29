using System.Globalization;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>Outgoing messages (section <c>Identity:Messaging</c>). The sender domain is an owner input (P1.8).</summary>
public sealed class MessagingOptions
{
    public const string Section = "Identity:Messaging";

    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 587;

    /// <summary>STARTTLS is required unless the server is the local Mailpit (development only).</summary>
    public bool RequireTls { get; set; } = true;

    public string? SmtpUsername { get; set; }

    public string? SmtpPassword { get; set; }

    public string From { get; set; } = "Karry <no-reply@karry.app>";

    /// <summary>
    /// <c>none</c> (default): phone invitations cannot be delivered until the owner chooses an SMS/WhatsApp provider.
    /// <c>email-preview</c> (local stack only): the text message is e-mailed to <c>sms+&lt;number&gt;@sms.karry.local</c>
    /// so it shows up in Mailpit.
    /// </summary>
    public string PhoneMode { get; set; } = "none";
}

/// <summary>Sends one message with the configured SMTP server (invitations, support notices).</summary>
internal static class Smtp
{
    public static async Task SendAsync(MessagingOptions settings, MimeMessage mail, CancellationToken cancellationToken)
    {
        var host = settings.SmtpHost ?? throw new InvalidOperationException("No SMTP host is configured.");
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(host, settings.SmtpPort,
            settings.RequireTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(settings.SmtpUsername))
        {
            await smtp.AuthenticateAsync(settings.SmtpUsername, settings.SmtpPassword ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await smtp.SendAsync(mail, cancellationToken).ConfigureAwait(false);
        await smtp.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsDeliveryFailure(Exception ex) =>
        ex is MailKit.ProtocolException or MailKit.CommandException or IOException or System.Net.Sockets.SocketException or AuthenticationException;
}

/// <summary>
/// Delivers invitations by e-mail (SMTP) and, until a provider is chosen, previews phone invitations in Mailpit. The
/// message is bilingual (FR, then EN): the invitee's language is unknown until they sign in. The link is never logged.
/// </summary>
internal sealed partial class InvitationSender(IOptions<MessagingOptions> options, ILogger<InvitationSender> logger) : IInvitationSender
{
    public async Task<bool> SendAsync(InvitationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
        {
            LogNotConfigured(logger, message.Channel);
            return false;
        }

        string recipient;
        if (message.Channel == InvitationChannel.Email)
        {
            recipient = message.Destination;
        }
        else if (string.Equals(settings.PhoneMode, "email-preview", StringComparison.Ordinal))
        {
            recipient = $"sms+{message.Destination.TrimStart('+')}@sms.karry.local";
        }
        else
        {
            LogNotConfigured(logger, message.Channel);
            return false;
        }

        using var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(settings.From));
        mail.To.Add(new MailboxAddress(message.RecipientName, recipient));
        mail.Subject = $"Invitation Karry · {message.CompanyName}";
        mail.Body = new TextPart("plain") { Text = Text(message) };

        try
        {
            await Smtp.SendAsync(settings, mail, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (Smtp.IsDeliveryFailure(ex))
        {
            LogDeliveryFailed(logger, message.Channel, ex);
            return false;
        }
    }

    internal static string Text(InvitationMessage message)
    {
        var expires = message.ExpiresAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return $"""
            Bonjour {message.RecipientName},

            {message.CompanyName} vous invite à rejoindre Karry. Ouvrez ce lien pour choisir votre mot de passe :
            {message.Link}
            Le lien est valable jusqu'au {expires} et ne fonctionne qu'une fois.

            ---

            Hello {message.RecipientName},

            {message.CompanyName} invites you to Karry. Open this link to choose your password:
            {message.Link}
            The link is valid until {expires} and works only once.
            """;
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Warning, Message = "Invitation by {Channel} not delivered: no provider configured")]
    private static partial void LogNotConfigured(ILogger logger, InvitationChannel channel);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Error, Message = "Invitation by {Channel} not delivered")]
    private static partial void LogDeliveryFailed(ILogger logger, InvitationChannel channel, Exception exception);
}
