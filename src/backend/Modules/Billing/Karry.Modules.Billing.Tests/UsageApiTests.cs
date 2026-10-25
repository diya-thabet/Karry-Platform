using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Outbox;
using Karry.Modules.Billing.Application;
using Karry.Modules.Tenancy.Infrastructure;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Billing.Tests;

/// <summary>
/// Usage meters and soft limits over HTTP (US-P1-BIL-04): live usage against the plan, creation refused only beyond
/// the 10 % grace, field capture never, and the daily snapshot with the over-limit notice.
/// </summary>
[Trait("Story", "US-P1-BIL-04")]
public sealed class UsageApiTests(BillingHost host) : IClassFixture<BillingHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P1-BIL-004")]
    public async Task Five_percent_over_shows_a_banner_only_and_fifteen_percent_over_blocks_creation_but_never_field_capture()
    {
        var tenant = await StarterTenantAsync(); // 20 assets per site, 1 site
        var token = TestTokens.ForTenant(tenant);

        GatedProbeModule.Assets[tenant] = 21;
        var over = await AssetsLineAsync(token);
        over.GetProperty("usage").GetInt64().Should().Be(21);
        over.GetProperty("limit").GetInt64().Should().Be(20);
        over.GetProperty("ceiling").GetInt64().Should().Be(22);
        over.GetProperty("state").GetString().Should().Be("over", "5 % over: the admin sees a banner");
        (await SendAsync(HttpMethod.Post, "/api/v1/probe/assets", token, new { })).StatusCode.Should().Be(HttpStatusCode.OK, "within the grace nothing is blocked");
        (await AssetsLineAsync(token)).GetProperty("state").GetString().Should().Be("blocked", "22 is the ceiling");

        GatedProbeModule.Assets[tenant] = 23;
        var refused = await SendAsync(HttpMethod.Post, "/api/v1/probe/assets", token, new { });
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await Json(refused);
        problem.GetProperty("code").GetString().Should().Be("billing.limit_reached");
        problem.GetProperty("metric").GetString().Should().Be("assets");
        problem.GetProperty("limit").GetInt64().Should().Be(20);
        problem.GetProperty("usage").GetInt64().Should().Be(23);
        GatedProbeModule.Assets[tenant].Should().Be(23, "nothing was created");

        (await SendAsync(HttpMethod.Post, "/api/v1/probe/field-readings", token, new { })).StatusCode.Should().Be(HttpStatusCode.OK, "field capture is never limited");
    }

    [Fact]
    public async Task More_sites_and_extra_assets_raise_the_limit_at_once()
    {
        var tenant = await StarterTenantAsync();
        var token = TestTokens.ForTenant(tenant);
        GatedProbeModule.Assets[tenant] = 30;
        (await AssetsLineAsync(token)).GetProperty("state").GetString().Should().Be("blocked");

        (await SendAsync(HttpMethod.Put, $"/api/v1/billing/tenants/{tenant}/subscription", null,
            new { planCode = "starter", period = "monthly", sites = 2, extraAssets = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);

        var line = await AssetsLineAsync(token);
        line.GetProperty("limit").GetInt64().Should().Be(45);
        line.GetProperty("state").GetString().Should().Be("within");
        (await SendAsync(HttpMethod.Post, "/api/v1/probe/assets", token, new { })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_daily_snapshot_keeps_one_value_per_metric_and_day_and_reports_tenants_over_their_limit()
    {
        var tenant = await StarterTenantAsync();
        var snapshot = host.Services.GetRequiredService<SnapshotUsage>();

        GatedProbeModule.Assets[tenant] = 12;
        (await snapshot.RunAsync(Ct)).Failed.Should().Be(0);
        GatedProbeModule.Assets[tenant] = 21;
        var run = await snapshot.RunAsync(Ct);

        run.Failed.Should().Be(0);
        run.OverLimit.Should().BeGreaterThanOrEqualTo(1);
        host.Services.GetRequiredService<UsageMetrics>().OverLimit.Should().Be(run.OverLimit);
        (await ScalarAsync($"SELECT count(*) FROM billing.usage_snapshots WHERE tenant_id = '{tenant}'")).Should().Be(1, "the same day is replaced");
        (await ScalarAsync($"SELECT value FROM billing.usage_snapshots WHERE tenant_id = '{tenant}' AND metric = 'assets'")).Should().Be(21);
        (await ScalarAsync($"SELECT \"limit\" FROM billing.usage_snapshots WHERE tenant_id = '{tenant}' AND metric = 'assets'")).Should().Be(20);

        var usage = await Json(await SendAsync(HttpMethod.Get, "/api/v1/billing/usage", TestTokens.ForTenant(tenant)));
        usage.GetProperty("snapshotOn").GetString().Should().Be(DateOnly.FromDateTime(host.Clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd"));

        host.Clock.Advance(TimeSpan.FromDays(1));
        await snapshot.RunAsync(Ct);
        (await ScalarAsync($"SELECT count(*) FROM billing.usage_snapshots WHERE tenant_id = '{tenant}'")).Should().Be(2, "one per day");
    }

    [Fact]
    public async Task Usage_is_for_the_company_s_billing_readers_only()
    {
        var tenant = await StarterTenantAsync();

        (await SendAsync(HttpMethod.Get, "/api/v1/billing/usage", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "an operator has no tenant");
        (await SendAsync(HttpMethod.Get, "/api/v1/billing/usage", TestTokens.ForTenant(tenant))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<JsonElement> AssetsLineAsync(string token)
    {
        var usage = await Json(await SendAsync(HttpMethod.Get, "/api/v1/billing/usage", token));
        return usage.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("metric").GetString() == "assets");
    }

    private async Task<Guid> StarterTenantAsync()
    {
        var slug = $"u-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", null, new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await Json(response)).GetProperty("id").GetGuid();
        var dispatcher = host.Services.GetRequiredService<OutboxDispatcher<TenancyDbContext>>();
        await dispatcher.DispatchPendingAsync(100, Ct);
        (await SendAsync(HttpMethod.Put, $"/api/v1/billing/tenants/{tenant}/subscription", null,
            new { planCode = "starter", period = "monthly", sites = 1, extraAssets = 0 })).StatusCode.Should().Be(HttpStatusCode.OK);
        return tenant;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
            request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForPlatformOperator());
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
