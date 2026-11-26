namespace Karry.BuildingBlocks.Messaging;

/// <summary>One plain-text e-mail to one person (ADR-0039 §4). The address and the text are never logged (T-18).</summary>
public sealed record EmailMessage(string To, string? ToName, string Subject, string Text);

/// <summary>What became of a send.</summary>
public enum EmailOutcome
{
    /// <summary>The mail server accepted it.</summary>
    Sent,

    /// <summary>No mail server is configured in this environment: nothing was tried.</summary>
    NotConfigured,

    /// <summary>The server could not be reached or refused it: worth trying again later.</summary>
    Failed,
}

/// <summary>
/// The port every module sends e-mail through (ADR-0039 §4): SMTP behind it (Mailpit locally, the provider chosen with
/// the deployment). It never throws for a delivery failure: it answers <see cref="EmailOutcome.Failed"/>, and the caller
/// keeps its delivery record to try again.
/// </summary>
public interface IEmailSender
{
    Task<EmailOutcome> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
