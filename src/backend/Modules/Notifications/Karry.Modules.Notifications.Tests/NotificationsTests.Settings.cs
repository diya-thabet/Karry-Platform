using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Notifications.Contracts;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Notifications.Tests;

/// <summary>
/// The company's rule switches, each person's channel choices (ADR-0039 §2) and what a notice may carry (§6,
/// TC-P2-NOT-004).
/// </summary>
public sealed partial class NotificationsTests
{
    [Fact]
    [Trait("TestCase", "TC-P2-NOT-004")]
    public async Task A_notice_keeps_only_the_identifiers_and_codes_its_rule_names_whatever_the_producer_sent()
    {
        var tenant = await ProvisionAsync();
        var (site, manager) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.Holders[("site-manager", site)] = [manager];
        host.People.UserOf[$"mgr-{tenant}"] = manager;
        var asset = Guid.CreateVersion7();

        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.AssetUnsafe, new Dictionary<string, string>
        {
            ["assetId"] = asset.ToString("D"),
            ["items"] = "brakes,lights",
            ["unitPrice"] = "1250.00",
            ["operatorName"] = "Mamadou Diallo",
        }, "/assets/holds", site));
        await PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.AssetUnsafe, new Dictionary<string, string>
        {
            ["assetId"] = "the loader of Mamadou",
            ["items"] = new string('x', 201),
        }, "/assets/holds", site));

        var items = (await MineAsync(tenant, $"mgr-{tenant}")).GetProperty("items").EnumerateArray()
            .Select(i => string.Join(';', i.GetProperty("parameters").EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}").Order(StringComparer.Ordinal))).ToList();
        items.Should().Equal("", $"assetId={asset:D};items=brakes,lights");
    }

    [Fact]
    public async Task A_company_switches_a_rule_off_with_a_recent_sign_in_ledgered_and_nobody_hears_of_that_type_until_it_is_on_again()
    {
        var tenant = await ProvisionAsync();
        var (site, manager) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        host.People.Holders[("site-manager", site)] = [manager];
        host.People.UserOf[$"mgr-{tenant}"] = manager;
        var unsafeMachine = () => PublishAsync(tenant, new Notice(Guid.CreateVersion7(), NoticeTypes.AssetUnsafe, new Dictionary<string, string>(), "/assets/holds", site));

        using var stale = await PutAsync(TestTokens.ForTenant(tenant, $"adm-{tenant}"), "/api/v1/notifications/rules/asset.unsafe", enabled: false);
        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "switching an alert off needs a recent sign-in");
        using var off = await PutAsync(Recent(tenant, $"adm-{tenant}"), "/api/v1/notifications/rules/asset.unsafe", enabled: false);
        off.StatusCode.Should().Be(HttpStatusCode.OK, await off.Content.ReadAsStringAsync(Ct));
        await unsafeMachine();
        (await TypesAsync(tenant, "mgr")).Should().BeEmpty();

        var rules = await GetAsync(tenant, $"adm-{tenant}", "/api/v1/notifications/rules");
        var rule = rules.EnumerateArray().Single(r => r.GetProperty("type").GetString() == "asset.unsafe");
        (rule.GetProperty("enabled").GetBoolean(), rule.GetProperty("changedBy").GetString()).Should().Be((false, $"adm-{tenant}"));
        rules.EnumerateArray().Where(r => r.GetProperty("type").GetString() != "asset.unsafe").Should().OnlyContain(r => r.GetProperty("enabled").GetBoolean());
        var otherCompany = await ProvisionAsync();
        (await GetAsync(otherCompany, $"adm-{otherCompany}", "/api/v1/notifications/rules")).EnumerateArray()
            .Should().OnlyContain(r => r.GetProperty("enabled").GetBoolean(), "a company's switch is its own");

        using var on = await PutAsync(TestTokens.ForTenant(tenant, $"adm-{tenant}"), "/api/v1/notifications/rules/asset.unsafe", enabled: true);
        on.StatusCode.Should().Be(HttpStatusCode.OK, "switching an alert back on needs no step-up");
        await unsafeMachine();
        (await TypesAsync(tenant, "mgr")).Should().Equal("asset.unsafe");

        (await RowsAsync($"SELECT (payload::jsonb->>'enabled') || '|' || (payload::jsonb->>'actor') FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'notifications.rule_switched.v1' ORDER BY seq"))
            .Should().Equal($"false|adm-{tenant}", $"true|adm-{tenant}");
        using var unknown = await PutAsync(Recent(tenant, $"adm-{tenant}"), "/api/v1/notifications/rules/nobody.listens", enabled: false);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_person_turns_their_own_optional_channels_off_never_the_in_app_centre_and_never_anothers()
    {
        var tenant = await ProvisionAsync();
        host.People.UserOf[$"me-{tenant}"] = Guid.CreateVersion7();
        host.People.UserOf[$"col-{tenant}"] = Guid.CreateVersion7();

        var before = await GetAsync(tenant, $"me-{tenant}", "/api/v1/notifications/preferences");
        Channels(before, "review.rejected").Should().Be("push=True");
        Channels(before, "review.escalated").Should().Be("digest=True");

        using var off = await PutAsync(TestTokens.ForTenant(tenant, $"me-{tenant}"), "/api/v1/notifications/preferences/review.rejected/push", enabled: false);
        off.StatusCode.Should().Be(HttpStatusCode.OK);
        Channels(await off.Content.ReadFromJsonAsync<JsonElement>(Ct), "review.rejected").Should().Be("push=False");
        using var again = await PutAsync(TestTokens.ForTenant(tenant, $"me-{tenant}"), "/api/v1/notifications/preferences/review.rejected/push", enabled: false);
        again.StatusCode.Should().Be(HttpStatusCode.OK, "setting the same choice twice is harmless");
        Channels(await GetAsync(tenant, $"col-{tenant}", "/api/v1/notifications/preferences"), "review.rejected").Should().Be("push=True");

        using var inApp = await PutAsync(TestTokens.ForTenant(tenant, $"me-{tenant}"), "/api/v1/notifications/preferences/review.rejected/in_app", enabled: false);
        using var notSent = await PutAsync(TestTokens.ForTenant(tenant, $"me-{tenant}"), "/api/v1/notifications/preferences/review.rejected/digest", enabled: false);
        using var unknown = await PutAsync(TestTokens.ForTenant(tenant, $"me-{tenant}"), "/api/v1/notifications/preferences/nobody.listens/push", enabled: false);
        using var stranger = await PutAsync(TestTokens.ForTenant(tenant, $"nobody-{tenant}"), "/api/v1/notifications/preferences/review.rejected/push", enabled: false);
        (inApp.StatusCode, notSent.StatusCode, unknown.StatusCode, stranger.StatusCode)
            .Should().Be((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Forbidden));
    }

    private static string Channels(JsonElement preferences, string type) =>
        string.Join(';', preferences.EnumerateArray().Single(p => p.GetProperty("type").GetString() == type)
            .GetProperty("channels").EnumerateObject().Select(c => $"{c.Name}={c.Value.GetBoolean()}"));

    private async Task<JsonElement> GetAsync(Guid tenant, string subject, string path)
    {
        using var response = await SendAsync(HttpMethod.Get, tenant, subject, path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HttpResponseMessage> PutAsync(string token, string path, bool enabled)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(new { enabled }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<List<string>> RowsAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(host.Database.Admin);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static string Recent(Guid tenant, string subject) =>
        TestTokens.Create(subject, (TenantResolutionMiddleware.TenantClaim, tenant.ToString()),
            (PermissionMiddleware.AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
}
