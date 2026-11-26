using Karry.Modules.Notifications.Application;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Host.IntegrationTests;

/// <summary>The API host with its e-mail sender pointing at a Mailpit container (ADR-0039 §4).</summary>
public sealed class MailSyncHost : SyncHost
{
    private MailpitServer? mailpit;

    public MailpitServer Mailpit => mailpit!;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (mailpit is not null)
        {
            await mailpit.DisposeAsync();
        }
    }

    protected override async Task<IReadOnlyDictionary<string, string?>> StartStorageAsync()
    {
        mailpit = await MailpitServer.StartAsync();
        return mailpit.SenderSettings;
    }
}

/// <summary>
/// P2.10 "done when" (plan §2): a digest reaches Mailpit. At 07:30 in the site's time zone the site's manager gets one
/// e-mail with their digest notices of the last day, through the real modules (Organization's sites, Identity's people
/// and SMTP sender, Notifications' rules, digests and deliveries) to the mail server.
/// </summary>
[Trait("Story", "US-P2-NOT-01")]
public sealed class DigestMailTests(MailSyncHost host) : IClassFixture<MailSyncHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P2-NOT-003")]
    public async Task At_seven_thirty_site_time_the_sites_manager_finds_the_digest_in_the_mailbox()
    {
        var company = await host.CompanyAsync("digest");
        var manager = Guid.CreateVersion7();
        var address = $"manager-{company.Slug}@sync.test";
        await host.ExecuteAsync(
            """
            INSERT INTO identity.users (id, tenant_id, kind, status, first_name, last_name, email, subject, roles, created_at)
            VALUES ($1, $2, 'Office', 'Active', 'Awa', 'Camara', $3, $4, $5::jsonb, now())
            """, manager, company.Id, address, $"mgr-{company.Slug}", $$"""[{"role":"site-manager","siteId":"{{company.North}}"}]""");

        // The sites are in Africa/Tunis (UTC+1): 06:30 UTC is 07:30 there, and the digest covers yesterday 07:00 to today 07:00.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = new DateTimeOffset(today.ToDateTime(new TimeOnly(6, 30)), TimeSpan.Zero);
        await host.ExecuteAsync(
            """
            INSERT INTO notifications.notifications (id, tenant_id, recipient_id, type, parameters, link, site_id, source_event_id, created_at)
            VALUES (gen_random_uuid(), $1, $2, 'integrity.missing_end', '{}'::jsonb, '/assets/integrity', $3, gen_random_uuid(), $4)
            """, company.Id, manager, company.North, now.AddHours(-4));

        (await host.Services.GetRequiredService<QueueDigests>().RunAtAsync(now, Ct)).Failed.Should().Be(0);
        (await host.Services.GetRequiredService<DeliverEmails>().RunAtAsync(DateTimeOffset.UtcNow, Ct)).Failed.Should().Be(0);

        var mail = await host.Mailpit.MessageToAsync(address, Ct);
        mail.GetProperty("Subject").GetString().Should().Be($"Karry · NORD · {today:yyyy-MM-dd}");
        mail.GetProperty("To")[0].GetProperty("Name").GetString().Should().Be("Awa Camara");
        mail.GetProperty("Text").GetString().Should()
            .Contain($"Résumé du {today:yyyy-MM-dd} pour NORD · Carrière NORD").And.Contain("- Fins de poste manquantes : 1")
            .And.Contain($"Digest of {today:yyyy-MM-dd} for NORD · Carrière NORD").And.Contain("- Missing shift ends : 1")
            .And.Contain($"{company.Slug}").And.Contain("/notifications");
    }
}
