using Karry.BuildingBlocks.Messaging;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Notifications.Application;
using Karry.Modules.Notifications.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Notifications.Tests;

/// <summary>
/// E-mail deliveries (ADR-0039 §4): each e-mail is a record with its attempts; the address is looked up at each attempt;
/// a failure is tried again with backoff and given up after six; a person without an address is given up at once.
/// </summary>
public sealed partial class NotificationsTests
{
    [Fact]
    public async Task A_queued_e_mail_goes_to_the_persons_current_address_once()
    {
        var tenant = await ProvisionAsync();
        var person = Guid.CreateVersion7();
        host.People.Contacts[person] = new NoticeContact($"manager-{tenant:N}@karry.test", "Awa Camara");
        var key = await QueueAsync(tenant, person);

        await RunAsync(DateTimeOffset.UtcNow);
        await RunAsync(DateTimeOffset.UtcNow.AddHours(1));

        host.Mailbox.Received.Where(m => m.To == $"manager-{tenant:N}@karry.test").Should().ContainSingle()
            .Which.Should().Be(new EmailMessage($"manager-{tenant:N}@karry.test", "Awa Camara", "Karry · digest", "3 entries to review"));
        var delivery = await DeliveryAsync(tenant, key);
        (delivery.Status, delivery.Attempts, delivery.LastError).Should().Be((DeliveryStatus.Sent, 1, (string?)null));
        delivery.SentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_failed_send_is_tried_again_after_its_backoff_and_not_before()
    {
        var tenant = await ProvisionAsync();
        var person = Guid.CreateVersion7();
        host.People.Contacts[person] = new NoticeContact($"retry-{tenant:N}@karry.test", "Retry");
        var key = await QueueAsync(tenant, person);
        host.Mailbox.Answer($"retry-{tenant:N}@karry.test", EmailOutcome.Failed);

        var start = DateTimeOffset.UtcNow;
        await RunAsync(start);
        var failed = await DeliveryAsync(tenant, key);
        (failed.Status, failed.Attempts, failed.LastError).Should().Be((DeliveryStatus.Pending, 1, "smtp_failed"));
        failed.NextAttemptAt.Should().BeAfter(start.AddSeconds(50), "the first retry waits a minute");

        await RunAsync(start.AddSeconds(10));
        host.Mailbox.Received.Where(m => m.To == $"retry-{tenant:N}@karry.test").Should().BeEmpty("the backoff is not over");
        await RunAsync(start.AddMinutes(2));
        host.Mailbox.Received.Where(m => m.To == $"retry-{tenant:N}@karry.test").Should().ContainSingle();
        (await DeliveryAsync(tenant, key)).Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_person_without_an_address_is_given_up_at_once_and_an_unconfigured_server_after_six_attempts()
    {
        var tenant = await ProvisionAsync();
        var phoneOnly = await QueueAsync(tenant, Guid.CreateVersion7());
        var person = Guid.CreateVersion7();
        host.People.Contacts[person] = new NoticeContact($"never-{tenant:N}@karry.test", "Never");
        var unconfigured = await QueueAsync(tenant, person);

        var at = DateTimeOffset.UtcNow;
        for (var attempt = 0; attempt < EmailDelivery.MaxAttempts; attempt++)
        {
            host.Mailbox.Answer($"never-{tenant:N}@karry.test", EmailOutcome.NotConfigured);
            await RunAsync(at);
            at = at.AddHours(5);
        }

        var noAddress = await DeliveryAsync(tenant, phoneOnly);
        (noAddress.Status, noAddress.Attempts, noAddress.LastError).Should().Be((DeliveryStatus.Failed, 1, "no_address"));
        var givenUp = await DeliveryAsync(tenant, unconfigured);
        (givenUp.Status, givenUp.Attempts, givenUp.LastError).Should().Be((DeliveryStatus.Failed, EmailDelivery.MaxAttempts, "not_configured"));
        host.Mailbox.Received.Where(m => m.To == $"never-{tenant:N}@karry.test").Should().BeEmpty();
    }

    private async Task<string> QueueAsync(Guid tenant, Guid person)
    {
        var key = $"test:{Guid.NewGuid():N}";
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var store = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        (await store.EmailQueuedAsync(key, Ct)).Should().BeFalse();
        store.Add(EmailDelivery.Queue(TimeProvider.System, new TenantId(tenant), person, "digest", key, "Karry · digest", "3 entries to review"));
        await store.SaveAsync(Ct);
        (await store.EmailQueuedAsync(key, Ct)).Should().BeTrue();
        return key;
    }

    private async Task RunAsync(DateTimeOffset now) =>
        (await host.Services.GetRequiredService<DeliverEmails>().RunAtAsync(now, Ct)).Failed.Should().Be(0);

    private async Task<EmailDelivery> DeliveryAsync(Guid tenant, string key)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.NotificationsDbContext>();
        return await db.EmailDeliveries.AsNoTracking().SingleAsync(d => d.Key == key, Ct);
    }
}
