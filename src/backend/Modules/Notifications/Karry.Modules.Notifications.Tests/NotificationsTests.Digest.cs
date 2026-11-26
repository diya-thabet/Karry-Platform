using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Notifications.Application;
using Karry.Modules.Notifications.Contracts;
using Karry.Modules.Notifications.Domain;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Verification.Contracts;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Notifications.Tests;

/// <summary>
/// The daily digest (ADR-0039 §4, US-P2-VER-06 AC2, TC-P2-NOT-003): at 07:00 site time, one e-mail per person and site:
/// the site's review counts for its managers and the person's digest notices of the last day, counts and links only.
/// </summary>
public sealed partial class NotificationsTests
{
    [Fact]
    [Trait("TestCase", "TC-P2-NOT-003")]
    public async Task At_seven_site_time_each_manager_gets_one_digest_of_the_sites_counts_and_their_notices()
    {
        var tenant = await ProvisionAsync();
        var (conakry, tokyo) = (Site("CKY", "Africa/Conakry"), Site("TYO", "Asia/Tokyo"));
        host.Organization.OfCompany[tenant] = [conakry, tokyo];
        host.Organization.Counts[conakry.Id] = new ReviewCounts(3, 1, 2);
        host.Organization.Counts[tokyo.Id] = new ReviewCounts(5, 0, 0);
        var (manager, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.Holders[("site-manager", conakry.Id)] = [manager];
        host.People.Holders[("site-manager", tokyo.Id)] = [other];
        host.People.Contacts[manager] = new NoticeContact($"digest-{tenant:N}@karry.test", "Awa Camara");

        // 07:30 in Conakry (UTC+0) is 16:30 in Tokyo: only Conakry's digest is due.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = new DateTimeOffset(today.ToDateTime(new TimeOnly(7, 30)), TimeSpan.Zero);
        var inside = now.AddHours(-10);
        await NoticeAtAsync(tenant, NoticeTypes.MissingEnd, conakry.Id, inside);
        await NoticeAtAsync(tenant, NoticeTypes.MissingEnd, conakry.Id, inside.AddMinutes(5));
        await NoticeAtAsync(tenant, NoticeTypes.MissingEnd, conakry.Id, now.AddDays(-2)); // before this digest's day
        await NoticeAtAsync(tenant, NoticeTypes.AssetUnsafe, conakry.Id, inside); // pushed, never in a digest

        (await DigestsAsync(now)).Failed.Should().Be(0);
        (await DigestsAsync(now.AddMinutes(15))).Failed.Should().Be(0);

        var queued = await DigestsOfAsync(tenant);
        var digest = queued.Should().ContainSingle("one per person, site and day, however often the job runs").Which;
        (digest.RecipientId, digest.Key).Should().Be((manager, $"digest:{manager:N}:{conakry.Id:N}:{today:yyyy-MM-dd}"));
        digest.Subject.Should().Be($"Karry · CKY · {today:yyyy-MM-dd}");
        var slug = (await TenantAsync(tenant)).Slug;
        digest.Text.Should()
            .Contain($"Résumé du {today:yyyy-MM-dd} pour CKY · Site CKY").And.Contain("- Saisies à contrôler : 3").And.Contain("- Dont en retard : 1")
            .And.Contain("- Corrigées depuis hier 07:00 : 2").And.Contain("- Fins de poste manquantes : 2")
            .And.Contain($"Digest of {today:yyyy-MM-dd} for CKY · Site CKY").And.Contain("- Entries to review : 3").And.Contain("- Missing shift ends : 2")
            .And.Contain($"https://{slug}.karry.test/review").And.Contain($"https://{slug}.karry.test/notifications")
            .And.NotContain("unsafe").And.NotContain("Tokyo");

        await host.Services.GetRequiredService<DeliverEmails>().RunAtAsync(DateTimeOffset.UtcNow, Ct);
        host.Mailbox.Received.Should().ContainSingle(m => m.To == $"digest-{tenant:N}@karry.test").Which.Subject.Should().Be(digest.Subject);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-NOT-003")]
    public async Task A_person_who_turned_the_digest_off_and_a_quiet_site_get_nothing_and_a_notice_alone_is_enough()
    {
        var tenant = await ProvisionAsync();
        var (busy, quiet) = (Site("BSY", "Europe/Paris"), Site("QUI", "Europe/Paris"));
        host.Organization.OfCompany[tenant] = [busy, quiet];
        host.Organization.Counts[busy.Id] = new ReviewCounts(4, 0, 0);
        var (optedOut, quietManager, executive) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.Holders[("site-manager", busy.Id)] = [optedOut];
        host.People.Holders[("site-manager", quiet.Id)] = [quietManager];
        host.People.Holders[("executive", null)] = [executive];
        host.People.UserOf[$"out-{tenant}"] = optedOut;
        using (var off = await PutAsync(TestTokens.ForTenant(tenant, $"out-{tenant}"), "/api/v1/notifications/preferences/review.escalated/digest", enabled: false))
        {
            off.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        }

        // 07:15 in Paris, in winter or summer: the digest is due at 07:00 local whatever the offset.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var paris = NodaTime.DateTimeZoneProviders.Tzdb["Europe/Paris"];
        var now = NodaTime.LocalDate.FromDateOnly(today).At(new NodaTime.LocalTime(7, 15)).InZoneLeniently(paris).ToInstant().ToDateTimeOffset();
        await NoticeAtAsync(tenant, NoticeTypes.FraudSuspected, busy.Id, now.AddHours(-3));

        await DigestsAsync(now);

        var queued = await DigestsOfAsync(tenant);
        queued.Select(d => d.RecipientId).Should().Equal([executive], "the opted-out manager has no notice, the quiet site nothing to say");
        queued[0].Text.Should().Contain("- Soupçons de fraude : 1").And.NotContain("Saisies à contrôler");
    }

    [Fact]
    public void The_digest_covers_the_local_day_before_seven_and_is_made_only_from_seven_to_noon()
    {
        // The night Paris moves to summer time (2027-03-28): from yesterday 07:00 to today 07:00 is 23 hours.
        var spring = DailyDigests.Window("Europe/Paris", new DateTimeOffset(2027, 3, 28, 5, 30, 0, TimeSpan.Zero))!;
        (spring.Day, spring.To - spring.From).Should().Be((new DateOnly(2027, 3, 28), TimeSpan.FromHours(23)));
        spring.To.Should().Be(new DateTimeOffset(2027, 3, 28, 5, 0, 0, TimeSpan.Zero));
        (spring.From.Offset, spring.To.Offset).Should().Be((TimeSpan.Zero, TimeSpan.Zero), "the database takes UTC only");

        DailyDigests.Window("Africa/Conakry", new DateTimeOffset(2027, 1, 10, 6, 59, 0, TimeSpan.Zero)).Should().BeNull();
        DailyDigests.Window("Africa/Conakry", new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero)).Should().BeNull();
        DailyDigests.Window("Not/AZone", new DateTimeOffset(2027, 1, 10, 7, 0, 0, TimeSpan.Zero))!.Day.Should().Be(new DateOnly(2027, 1, 10), "an unknown zone is read as UTC");
    }

    private static SiteSummary Site(string code, string zone) => new(Guid.CreateVersion7(), code, $"Site {code}", zone, Archived: false);

    private async Task NoticeAtAsync(Guid tenant, string type, Guid site, DateTimeOffset at)
    {
        var notice = new Notice(Guid.CreateVersion7(), type, new Dictionary<string, string>(), "/assets/integrity", site);
        await PublishAsync(tenant, notice);
        await using var admin = new NpgsqlConnection(host.Database.Admin);
        await admin.OpenAsync(Ct);
        await using var move = new NpgsqlCommand("UPDATE notifications.notifications SET created_at = @at WHERE source_event_id = @source", admin);
        move.Parameters.AddWithValue("at", at);
        move.Parameters.AddWithValue("source", notice.SourceEventId);
        await move.ExecuteNonQueryAsync(Ct);
    }

    private async Task<DigestRun> DigestsAsync(DateTimeOffset now) => await host.Services.GetRequiredService<QueueDigests>().RunAtAsync(now, Ct);

    private async Task<List<EmailDelivery>> DigestsOfAsync(Guid tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.NotificationsDbContext>();
        return await db.EmailDeliveries.AsNoTracking().Where(d => d.Kind == DailyDigests.Kind).OrderBy(d => d.CreatedAt).ToListAsync(Ct);
    }

    private async Task<TenantSummary> TenantAsync(Guid tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync(new TenantId(tenant), Ct))!;
    }
}
