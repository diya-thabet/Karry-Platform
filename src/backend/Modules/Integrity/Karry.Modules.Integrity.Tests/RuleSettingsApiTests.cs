using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Integrity.Domain;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Integrity.Tests;

/// <summary>
/// A company's rule settings (ADR-0036 §1, US-P2-INT-01 AC3) over HTTP on real PostgreSQL: read by those who read
/// flags, changed by a company administrator who just signed in, never for a security rule's switch; every change is
/// ledgered; the rules then run with them, and another company keeps its own.
/// </summary>
[Trait("Story", "US-P2-INT-01")]
public sealed class RuleSettingsApiTests(IntegrityHost host) : IClassFixture<IntegrityHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_company_without_settings_runs_every_rule_of_the_catalogue_with_its_defaults()
    {
        var tenant = await ProvisionAsync();

        var rules = await RulesAsync(tenant);

        rules.Select(r => r.GetProperty("rule").GetString()).Should().Equal(
            "RULE-FO-01", "RULE-FO-02", "RULE-FO-03", "RULE-FO-04", "RULE-FO-05", "RULE-FO-06", "RULE-FO-07", "RULE-FO-09", "RULE-FO-10");
        rules.Should().OnlyContain(r => r.GetProperty("enabled").GetBoolean() && r.GetProperty("version").GetString() == "\"0\"");
        rules.Where(r => r.GetProperty("security").GetBoolean()).Select(r => r.GetProperty("rule").GetString()).Should().Equal("RULE-FO-05", "RULE-FO-06", "RULE-FO-07", "RULE-FO-09", "RULE-FO-10");
        var margin = Rule(rules, "RULE-FO-04").GetProperty("thresholds")[0];
        (margin.GetProperty("name").GetString(), margin.GetProperty("value").GetDecimal(), margin.GetProperty("unit").GetString(), margin.GetProperty("max").GetDecimal())
            .Should().Be(("marginMetres", 200m, "m", 5000m));
    }

    [Fact]
    public async Task A_changed_threshold_is_saved_ledgered_and_used_by_the_rule_and_a_stale_form_is_refused()
    {
        var tenant = await ProvisionAsync();

        using var saved = await PutAsync(tenant, "RULE-FO-04", new { enabled = true, thresholds = new { marginMetres = 500 } }, "\"0\"");
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("thresholds")[0].GetProperty("value").GetDecimal().Should().Be(500m);
        saved.Headers.ETag!.Tag.Should().Be(body.GetProperty("version").GetString()).And.NotBe("\"0\"");
        body.GetProperty("changedBy").GetString().Should().Be("admin-1");

        using var stale = await PutAsync(tenant, "RULE-FO-04", new { enabled = false, thresholds = (object?)null }, "\"0\"");
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await SettingsAsync(tenant, IntegrityRules.Geofence)).Should().Be((true, 500m));
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'integrity.rule_settings_changed.v1'")).Should().Be(1);
    }

    [Fact]
    public async Task A_security_rule_cannot_be_switched_off_by_the_api_nor_in_the_database()
    {
        var tenant = await ProvisionAsync();

        using var refused = await PutAsync(tenant, "RULE-FO-07", new { enabled = false, thresholds = (object?)null });
        (await ProblemCodeAsync(refused)).Should().Be("integrity.rule_locked");
        (await SettingsAsync(tenant, IntegrityRules.RevokedDevice)).Enabled.Should().BeTrue();

        foreach (var rule in RuleCatalog.All.Where(r => r.Security))
        {
            await FluentActions.Awaiting(() => ExecuteAsync(
                    $"INSERT INTO integrity.rule_settings (id, tenant_id, rule, enabled, thresholds, changed_at) VALUES ('{Guid.CreateVersion7()}', '{tenant}', '{rule.Code}', false, '{{}}', now())"))
                .Should().ThrowAsync<PostgresException>($"a check constraint keeps {rule.Code} on");
        }
    }

    [Fact]
    public async Task Unknown_rules_and_thresholds_out_of_range_are_refused()
    {
        var tenant = await ProvisionAsync();

        using var unknown = await PutAsync(tenant, "RULE-XX-99", new { enabled = true, thresholds = (object?)null });
        using var range = await PutAsync(tenant, "RULE-FO-03", new { enabled = true, thresholds = new { maxSkewMinutes = 0 } });
        using var name = await PutAsync(tenant, "RULE-FO-03", new { enabled = true, thresholds = new { marginMetres = 10 } });
        using var decimals = await PutAsync(tenant, "RULE-FO-02", new { enabled = true, thresholds = new { toleranceHours = 0.25m } });

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(range)).Should().Be("integrity.threshold_invalid");
        (await ProblemCodeAsync(name)).Should().Be("integrity.threshold_unknown");
        (await ProblemCodeAsync(decimals)).Should().Be("integrity.threshold_invalid");
    }

    [Fact]
    public async Task A_switched_off_rule_raises_nothing_new_and_its_open_flags_stay_open()
    {
        var tenant = await ProvisionAsync();
        var (meter, site) = (new FlagSubject("meter", Guid.CreateVersion7()), Guid.CreateVersion7());
        await SyncAsync(tenant, meter, site, ["a>b"]);

        using var off = await PutAsync(tenant, "RULE-FO-01", new { enabled = false, thresholds = (object?)null });
        off.StatusCode.Should().Be(HttpStatusCode.OK);
        await SyncAsync(tenant, meter, site, ["c>d"]);
        await SyncAsync(tenant, new FlagSubject("meter", Guid.CreateVersion7()), site, ["e>f"]);

        (await ScalarAsync<string>($"SELECT string_agg(key || ':' || (closed_at IS NULL), ',') FROM integrity.flags WHERE tenant_id = '{tenant}'"))
            .Should().Be("a>b:true");
    }

    [Fact]
    public async Task Changing_a_rule_needs_a_recent_sign_in_and_the_administrator_reading_needs_the_flags_permission()
    {
        var tenant = await ProvisionAsync();
        host.SiteOf[IntegrityHost.SiteManager] = Guid.CreateVersion7();
        var body = new { enabled = false, thresholds = (object?)null };

        using var longAgo = await PutAsync(tenant, "RULE-FO-01", body, token: TestTokens.ForTenant(tenant, "admin-1"));
        using var manager = await PutAsync(tenant, "RULE-FO-01", body, token: Recent(tenant, IntegrityHost.SiteManager));
        using var managerReads = await GetAsync(tenant, TestTokens.ForTenant(tenant, IntegrityHost.SiteManager));
        using var field = await GetAsync(tenant, TestTokens.ForTenant(tenant, IntegrityHost.Operator));

        longAgo.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a rule decides what gets flagged: step-up first");
        manager.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        managerReads.StatusCode.Should().Be(HttpStatusCode.OK);
        field.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SettingsAsync(tenant, IntegrityRules.Continuity)).Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Another_company_keeps_its_own_settings()
    {
        var (tenant, other) = (await ProvisionAsync(), await ProvisionAsync());

        using var saved = await PutAsync(tenant, "RULE-FO-03", new { enabled = true, thresholds = new { maxSkewMinutes = 45 } });
        saved.StatusCode.Should().Be(HttpStatusCode.OK);

        (await SettingsAsync(other, IntegrityRules.ClockSkew)).Should().Be((true, 10m));
        Rule(await RulesAsync(other), "RULE-FO-03").GetProperty("thresholds")[0].GetProperty("value").GetDecimal().Should().Be(10m);
        (await ScalarAsync<long>($"SELECT count(*) FROM integrity.rule_settings WHERE tenant_id = '{other}'")).Should().Be(0);
    }

    private static JsonElement Rule(IEnumerable<JsonElement> rules, string code) => rules.Single(r => r.GetProperty("rule").GetString() == code);

    private static string Recent(Guid tenant, string subject) =>
        TestTokens.Create(subject, (TenantResolutionMiddleware.TenantClaim, tenant.ToString()),
            (PermissionMiddleware.AuthTimeClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

    /// <summary>The rule's settings as another module reads them (enabled, first threshold or 0).</summary>
    private async Task<(bool Enabled, decimal Threshold)> SettingsAsync(Guid tenant, string rule)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var settings = await scope.ServiceProvider.GetRequiredService<IIntegrityFlags>().SettingsAsync(rule, Ct);
        return (settings.Enabled, settings.Thresholds.Values.FirstOrDefault());
    }

    private async Task SyncAsync(Guid tenant, FlagSubject subject, Guid site, IReadOnlyList<string> keys)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        await using var connection = new NpgsqlConnection(host.Database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IIntegrityFlags>().SyncAsync(connection, transaction, subject, IntegrityRules.Continuity, site,
            [.. keys.Select(k => new Finding(k, [], [], new Dictionary<string, string>()))], Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task<List<JsonElement>> RulesAsync(Guid tenant)
    {
        using var response = await GetAsync(tenant, TestTokens.ForTenant(tenant, "admin-1"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()];
    }

    private async Task<HttpResponseMessage> GetAsync(Guid tenant, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integrity/rules");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> PutAsync(Guid tenant, string rule, object body, string? ifMatch = null, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/integrity/rules/{rule}") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Recent(tenant, "admin-1"));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"r-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
