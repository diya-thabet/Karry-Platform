using Karry.Modules.Notifications.Application;
using Karry.Modules.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Notifications.Tests;

/// <summary>Notices are kept 90 days (ADR-0039 §1): older notifications, e-mails and pushes are deleted every night.</summary>
public sealed partial class NotificationsTests
{
    [Fact]
    public async Task Notices_e_mails_and_pushes_older_than_ninety_days_are_deleted_and_younger_ones_kept()
    {
        var tenant = await ProvisionAsync();
        var (me, site) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.UserOf[$"me-{tenant}"] = me;
        host.People.Holders[("site-manager", site)] = [me];
        host.Push.OnFor[tenant] = true;
        using (var subscribed = await SendJsonAsync(System.Net.Http.HttpMethod.Put, Karry.Testing.TestTokens.ForTenant(tenant, $"me-{tenant}"),
            "/api/v1/notifications/push/subscription", new { endpoint = $"https://fcm.googleapis.com/fcm/send/old-{tenant:N}" }))
        {
            subscribed.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);
        }

        var old = new Notice(Guid.CreateVersion7(), NoticeTypes.AssetUnsafe, new Dictionary<string, string>(), "/assets/holds", site);
        await PublishAsync(tenant, old);
        await QueueAsync(tenant, me);
        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.MissingEnd, new Dictionary<string, string>(), "/assets/integrity", site));
        await AgeAsync(tenant, "notifications", $"source_event_id = '{old.SourceEventId}'");
        await AgeAsync(tenant, "email_deliveries", "true");
        await AgeAsync(tenant, "push_pings", "true");

        var deleted = await host.Services.GetRequiredService<PurgeNotices>().RunAtAsync(DateTimeOffset.UtcNow, Ct);

        deleted.Should().BeGreaterThanOrEqualTo(3);
        (await CountAsync(tenant, "notifications")).Should().Be(1, "the younger notice stays");
        (await CountAsync(tenant, "email_deliveries")).Should().Be(0);
        (await CountAsync(tenant, "push_pings")).Should().Be(0);
    }

    private async Task AgeAsync(Guid tenant, string table, string where)
    {
        await using var admin = new NpgsqlConnection(host.Database.Admin);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"UPDATE notifications.{table} SET created_at = now() - interval '91 days' WHERE tenant_id = '{tenant}' AND {where}", admin);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> CountAsync(Guid tenant, string table)
    {
        await using var admin = new NpgsqlConnection(host.Database.Admin);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM notifications.{table} WHERE tenant_id = '{tenant}'", admin);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
