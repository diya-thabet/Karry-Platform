using Karry.BuildingBlocks.Messaging;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Notifications.Application;

/// <summary>
/// Sends the current company's e-mails that are due (ADR-0039 §4): the address is looked up now (a person who left or
/// has none is given up), and each outcome is saved before the next send, so a crash never sends one twice.
/// </summary>
public sealed class EmailDeliveries(INotificationStore store, INoticeAudiences audiences, IEmailSender sender, TimeProvider clock)
{
    /// <summary>At most this many e-mails per company and run; the rest wait for the next minute.</summary>
    public const int BatchSize = 50;

    /// <summary>Sends what is due at <paramref name="now"/>; returns how many were sent.</summary>
    public async Task<int> SendDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var delivery in await store.DueEmailsAsync(now, BatchSize, cancellationToken).ConfigureAwait(false))
        {
            if (await audiences.ContactOfAsync(delivery.RecipientId, cancellationToken).ConfigureAwait(false) is not { } contact)
            {
                delivery.Failed(clock, "no_address", final: true);
            }
            else
            {
                switch (await sender.SendAsync(new EmailMessage(contact.Email, contact.Name, delivery.Subject, delivery.Text), cancellationToken).ConfigureAwait(false))
                {
                    case EmailOutcome.Sent:
                        delivery.Sent(clock);
                        sent++;
                        break;
                    case EmailOutcome.NotConfigured:
                        delivery.Failed(clock, "not_configured");
                        break;
                    default:
                        delivery.Failed(clock, "smtp_failed");
                        break;
                }
            }

            await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return sent;
    }
}

/// <summary>What a run of the e-mail deliveries did: e-mails sent, and companies whose run failed.</summary>
public sealed record EmailDeliveryRun(int Sent, int Failed);

/// <summary>Sends the due e-mails company by company (every minute, ADR-0039 §4); one company's failure does not stop the others.</summary>
public sealed partial class DeliverEmails(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DeliverEmails> logger)
{
    public Task<EmailDeliveryRun> RunAsync(CancellationToken cancellationToken) => RunAtAsync(clock.GetUtcNow(), cancellationToken);

    public async Task<EmailDeliveryRun> RunAtAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var sent = 0;
        var failed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                sent += await scope.ServiceProvider.GetRequiredService<EmailDeliveries>().SendDueAsync(now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogFailed(logger, ex, tenant.Value);
            }
        }

        return new EmailDeliveryRun(sent, failed);
    }

    [LoggerMessage(EventId = 7601, Level = LogLevel.Error, Message = "Sending the due e-mails of tenant {Tenant} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenant);
}
