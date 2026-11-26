using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Notifications.Application;
using Karry.Modules.Notifications.Contracts;
using Karry.Modules.Notifications.Domain;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Notifications.Tests;

/// <summary>
/// Web Push (ADR-0039 §5, US-P2-OFF-04 AC2): behind the company's flag, a person subscribes their browser and a tablet
/// itself for its site; a notice queues one empty push per subscription, at most one waiting; a rejection alerts the
/// site's tablets, another notice its recipients' browsers; a subscription that is gone is deleted.
/// </summary>
public sealed partial class NotificationsTests
{
    [Fact]
    [Trait("Story", "US-P2-OFF-04")]
    public async Task A_person_subscribes_while_push_is_on_and_each_notice_alerts_their_browser_once_until_it_is_sent()
    {
        var tenant = await ProvisionAsync();
        var (site, manager) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.Holders[("site-manager", site)] = [manager];
        host.People.UserOf[$"mgr-{tenant}"] = manager;
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{tenant:N}";
        var token = TestTokens.ForTenant(tenant, $"mgr-{tenant}");

        using (var off = await SendJsonAsync(HttpMethod.Get, token, "/api/v1/notifications/push/key"))
        using (var refused = await SendJsonAsync(HttpMethod.Put, token, "/api/v1/notifications/push/subscription", new { endpoint }))
        {
            (off.StatusCode, refused.StatusCode).Should().Be((HttpStatusCode.NotFound, HttpStatusCode.NotFound), "push is off for the company");
        }

        host.Push.OnFor[tenant] = true;
        using (var key = await SendJsonAsync(HttpMethod.Get, token, "/api/v1/notifications/push/key"))
        {
            (await key.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("publicKey").GetString().Should().Be("BKarryTestPublicKey");
        }

        using (var elsewhere = await SendJsonAsync(HttpMethod.Put, token, "/api/v1/notifications/push/subscription", new { endpoint = "https://169.254.169.254/latest" }))
        using (var stranger = await SendJsonAsync(HttpMethod.Put, TestTokens.ForTenant(tenant, $"nobody-{tenant}"), "/api/v1/notifications/push/subscription", new { endpoint }))
        using (var subscribed = await SendJsonAsync(HttpMethod.Put, token, "/api/v1/notifications/push/subscription", new { endpoint }))
        using (var again = await SendJsonAsync(HttpMethod.Put, token, "/api/v1/notifications/push/subscription", new { endpoint }))
        {
            (elsewhere.StatusCode, stranger.StatusCode, subscribed.StatusCode, again.StatusCode)
                .Should().Be((HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.NoContent, HttpStatusCode.NoContent));
        }

        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.AssetUnsafe, new Dictionary<string, string>(), "/assets/holds", site));
        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.AssetUnsafe, new Dictionary<string, string>(), "/assets/holds", site));
        (await PingsAsync(tenant)).Should().ContainSingle("one push waits per subscription, however many notices come");

        (await host.Services.GetRequiredService<DeliverPushes>().RunAtAsync(DateTimeOffset.UtcNow, Ct)).Failed.Should().Be(0);
        host.Push.Pinged.Count(e => e == endpoint).Should().Be(1);
        (await PingsAsync(tenant)).Single().Status.Should().Be(DeliveryStatus.Sent);

        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.MissingEnd, new Dictionary<string, string>(), "/assets/integrity", site));
        (await PingsAsync(tenant)).Should().HaveCount(1, "a notice without the push channel (a digest kind) is not pushed");

        using (var removed = await SendJsonAsync(HttpMethod.Post, token, "/api/v1/notifications/push/subscription/remove", new { endpoint }))
        {
            removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.AssetUnsafe, new Dictionary<string, string>(), "/assets/holds", site));
        (await PingsAsync(tenant)).Should().HaveCount(1, "an unsubscribed browser is not alerted");
    }

    [Fact]
    [Trait("Story", "US-P2-OFF-04")]
    public async Task A_rejection_alerts_the_sites_tablets_unless_its_operator_turned_push_off_and_a_gone_tablet_is_forgotten()
    {
        var tenant = await ProvisionAsync();
        var (site, elsewhere, driver) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.UserOf[$"drv-{tenant}"] = driver;
        host.Push.OnFor[tenant] = true;
        var (tablet, other, far) = ($"https://web.push.apple.com/{tenant:N}-a", $"https://web.push.apple.com/{tenant:N}-b", $"https://web.push.apple.com/{tenant:N}-c");
        await DeviceSubscribesAsync(tenant, new DeviceCaller(Guid.CreateVersion7(), site, Revoked: false), tablet);
        var gone = new DeviceCaller(Guid.CreateVersion7(), site, Revoked: false);
        await DeviceSubscribesAsync(tenant, gone, other);
        await DeviceSubscribesAsync(tenant, new DeviceCaller(Guid.CreateVersion7(), elsewhere, Revoked: false), far);
        host.Push.Answer(other, PushOutcome.Gone);

        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.ReviewRejected, new Dictionary<string, string>(), "/history", site) { Person = driver });
        await host.Services.GetRequiredService<DeliverPushes>().RunAtAsync(DateTimeOffset.UtcNow, Ct);

        host.Push.Pinged.Should().Contain(tablet).And.NotContain(far, "another site's tablet holds nothing of this site");
        await using (var scope = Scope(tenant))
        {
            (await scope.ServiceProvider.GetRequiredService<IPushStore>().SubscriptionAsync(other, Ct)).Should().BeNull("a subscription that is gone is deleted");
        }

        using (var off = await PutAsync(TestTokens.ForTenant(tenant, $"drv-{tenant}"), "/api/v1/notifications/preferences/review.rejected/push", enabled: false))
        {
            off.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var before = (await PingsAsync(tenant)).Count;
        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.ReviewRejected, new Dictionary<string, string>(), "/history", site) { Person = driver });
        (await PingsAsync(tenant)).Should().HaveCount(before, "the operator turned push off for rejections");

        await using (var scope = Scope(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<PushSubscriptions>().UnsubscribeDeviceAsync(gone, Ct);
        }
    }

    [Fact]
    public async Task A_failed_push_is_tried_again_after_a_minute_and_given_up_after_four_attempts()
    {
        var tenant = await ProvisionAsync();
        var (site, manager) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.Holders[("maintenance-planner", site)] = [manager];
        host.People.UserOf[$"pl-{tenant}"] = manager;
        host.Push.OnFor[tenant] = true;
        var endpoint = $"https://updates.push.services.mozilla.com/wpush/v2/{tenant:N}";
        using (var subscribed = await SendJsonAsync(HttpMethod.Put, TestTokens.ForTenant(tenant, $"pl-{tenant}"), "/api/v1/notifications/push/subscription", new { endpoint }))
        {
            subscribed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        for (var i = 0; i < PushPing.MaxAttempts; i++)
        {
            host.Push.Answer(endpoint, PushOutcome.Failed);
        }

        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.AssetUnsafe, new Dictionary<string, string>(), "/assets/holds", site));
        var at = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<DeliverPushes>().RunAtAsync(at, Ct);
        var first = (await PingsAsync(tenant)).Single();
        (first.Status, first.Attempts, first.LastError).Should().Be((DeliveryStatus.Pending, 1, "push_failed"));
        await host.Services.GetRequiredService<DeliverPushes>().RunAtAsync(at.AddSeconds(30), Ct);
        (await PingsAsync(tenant)).Single().Attempts.Should().Be(1, "the backoff is not over");

        for (var i = 1; i < PushPing.MaxAttempts; i++)
        {
            at = at.AddHours(1);
            await host.Services.GetRequiredService<DeliverPushes>().RunAtAsync(at, Ct);
        }

        var last = (await PingsAsync(tenant)).Single();
        (last.Status, last.Attempts).Should().Be((DeliveryStatus.Failed, PushPing.MaxAttempts));
        host.Push.Pinged.Should().NotContain(endpoint);
    }

    private async Task DeviceSubscribesAsync(Guid tenant, DeviceCaller device, string endpoint)
    {
        await using var scope = Scope(tenant);
        (await scope.ServiceProvider.GetRequiredService<PushSubscriptions>().SubscribeDeviceAsync(device, endpoint, Ct)).IsSuccess.Should().BeTrue();
    }

    private AsyncServiceScope Scope(Guid tenant)
    {
        var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        return scope;
    }

    private async Task<List<PushPing>> PingsAsync(Guid tenant)
    {
        await using var scope = Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<Infrastructure.NotificationsDbContext>().PushPings.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(Ct);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string token, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }
}
