using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Notifications.Contracts;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Notifications.Tests;

/// <summary>
/// Notifications (ADR-0039 §1–§3): a notice from another module reaches the audience its rule names, once per event
/// and person, in the producer's transaction; each person reads and marks their own, never another's, never another
/// company's.
/// </summary>
[Trait("Story", "US-P2-NOT-01")]
public sealed class NotificationsTests(NotificationsHost host) : IClassFixture<NotificationsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rejection_reaches_the_entrys_operator_once_however_often_its_event_is_delivered()
    {
        var tenant = await ProvisionAsync();
        var (operatorId, source) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.UserOf[$"op-{tenant}"] = operatorId;
        var notice = new Notice(source, NoticeTypes.ReviewRejected, new Dictionary<string, string> { ["reasonCode"] = "wrong_reading" }, "/history", Guid.CreateVersion7())
        {
            Person = operatorId,
        };

        await PublishAsync(tenant, notice);
        await PublishAsync(tenant, notice);
        var mine = await MineAsync(tenant, $"op-{tenant}");

        mine.GetProperty("unread").GetInt32().Should().Be(1);
        var item = mine.GetProperty("items").EnumerateArray().Single();
        (item.GetProperty("type").GetString(), item.GetProperty("link").GetString(), item.GetProperty("parameters").GetProperty("reasonCode").GetString())
            .Should().Be(("review.rejected", "/history", "wrong_reading"));
    }

    [Fact]
    [Trait("TestCase", "TC-P2-NOT-002")]
    public async Task A_site_notice_reaches_the_roles_of_that_site_only_and_a_company_notice_the_companys_roles()
    {
        var tenant = await ProvisionAsync();
        var (siteA, siteB) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var (managerA, plannerA, managerB, executive) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.Holders[("site-manager", siteA)] = [managerA];
        host.People.Holders[("maintenance-planner", siteA)] = [plannerA];
        host.People.Holders[("site-manager", siteB)] = [managerB];
        host.People.Holders[("executive", null)] = [executive];
        foreach (var (subject, user) in new[] { ("ma", managerA), ("pa", plannerA), ("mb", managerB), ("ex", executive) })
        {
            host.People.UserOf[$"{subject}-{tenant}"] = user;
        }

        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.AssetUnsafe, new Dictionary<string, string>(), "/assets/holds", siteA));
        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.FraudSuspected, new Dictionary<string, string>(), "/assets/integrity", siteB));
        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), "nobody.listens", new Dictionary<string, string>(), "/", siteA));

        (await TypesAsync(tenant, "ma")).Should().Equal("asset.unsafe");
        (await TypesAsync(tenant, "pa")).Should().Equal("asset.unsafe");
        (await TypesAsync(tenant, "mb")).Should().BeEmpty("a manager of site B hears nothing of site A");
        (await TypesAsync(tenant, "ex")).Should().Equal("integrity.fraud_suspected");
    }

    [Fact]
    public async Task A_person_reads_and_marks_their_own_notifications_never_anothers_nor_another_companys()
    {
        var tenant = await ProvisionAsync();
        var other = await ProvisionAsync();
        var (me, colleague) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.UserOf[$"me-{tenant}"] = me;
        host.People.UserOf[$"col-{tenant}"] = colleague;
        host.People.UserOf[$"me-{other}"] = me; // the same user ID in another company sees that company's notices only
        for (var i = 0; i < 3; i++)
        {
            await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.ReviewRejected, new Dictionary<string, string>(), "/history", null) { Person = me });
        }

        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.ReviewRejected, new Dictionary<string, string>(), "/history", null) { Person = colleague });
        var first = await MineAsync(tenant, $"me-{tenant}");
        var theirs = (await MineAsync(tenant, $"col-{tenant}")).GetProperty("items")[0].GetProperty("id").GetGuid();

        using var notMine = await SendAsync(HttpMethod.Post, tenant, $"me-{tenant}", $"/api/v1/notifications/{theirs}/read");
        using var read = await SendAsync(HttpMethod.Post, tenant, $"me-{tenant}", $"/api/v1/notifications/{first.GetProperty("items")[0].GetProperty("id").GetGuid()}/read");
        var afterOne = await MineAsync(tenant, $"me-{tenant}");
        using var all = await SendAsync(HttpMethod.Post, tenant, $"me-{tenant}", "/api/v1/notifications/read-all");

        first.GetProperty("unread").GetInt32().Should().Be(3);
        notMine.StatusCode.Should().Be(HttpStatusCode.NotFound);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        afterOne.GetProperty("unread").GetInt32().Should().Be(2);
        (await all.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("count").GetInt32().Should().Be(2);
        (await MineAsync(tenant, $"me-{tenant}")).GetProperty("unread").GetInt32().Should().Be(0);
        (await MineAsync(tenant, $"col-{tenant}")).GetProperty("unread").GetInt32().Should().Be(1, "a colleague's stay unread");
        (await MineAsync(other, $"me-{other}")).GetProperty("items").GetArrayLength().Should().Be(0, "another company never sees them");
        (await MineAsync(tenant, $"me-{tenant}", "?unread=true")).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_poll_of_an_unchanged_list_is_a_304_and_a_new_notice_changes_the_tag()
    {
        var tenant = await ProvisionAsync();
        var me = Guid.CreateVersion7();
        host.People.UserOf[$"me-{tenant}"] = me;
        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.ReviewRejected, new Dictionary<string, string>(), "/history", null) { Person = me });

        using var first = await SendAsync(HttpMethod.Get, tenant, $"me-{tenant}", "/api/v1/notifications/mine");
        var tag = first.Headers.ETag?.ToString();
        using var unchanged = await SendAsync(HttpMethod.Get, tenant, $"me-{tenant}", "/api/v1/notifications/mine", tag);
        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.ReviewRejected, new Dictionary<string, string>(), "/history", null) { Person = me });
        using var changed = await SendAsync(HttpMethod.Get, tenant, $"me-{tenant}", "/api/v1/notifications/mine", tag);

        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await changed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("unread").GetInt32().Should().Be(2);
    }

    /// <summary>A module handing over a notice in its own transaction, as the outbox dispatch does (ADR-0039 §1).</summary>
    private async Task PublishAsync(Guid tenant, Notice notice)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<INotices>().PublishAsync(connection, transaction, notice, Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task<List<string>> TypesAsync(Guid tenant, string person) =>
        [.. (await MineAsync(tenant, $"{person}-{tenant}")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()!)];

    private async Task<JsonElement> MineAsync(Guid tenant, string subject, string query = "")
    {
        using var response = await SendAsync(HttpMethod.Get, tenant, subject, $"/api/v1/notifications/mine{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Guid tenant, string subject, string path, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant, subject));
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"n-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }
}
