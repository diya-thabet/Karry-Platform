using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Flags.Tests;

/// <summary>
/// Flags over HTTP (US-P1-PLT-05, TC-P1-PLT-009): a change applies at once without a release, per company, per user
/// and platform-wide; company changes are in that company's ledger, platform-wide ones in the append-only history.
/// </summary>
[Trait("Story", "US-P1-PLT-05")]
public sealed class FlagsApiTests(FlagsHost host) : IClassFixture<FlagsHost>
{
    private const string Banner = "portal.usage-banner";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-009")]
    public async Task A_company_change_applies_at_once_only_to_that_company_and_is_in_its_ledger()
    {
        var a = await ProvisionAsync();
        var b = await ProvisionAsync();
        (await FlagAsync(a, "user-1")).Should().BeTrue("the declared default");

        (await SetAsync($"/api/v1/flags/tenants/{a}/{Banner}", new { enabled = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SetAsync($"/api/v1/flags/tenants/{a}/{Banner}", new { enabled = false })).StatusCode.Should().Be(HttpStatusCode.OK, "repeating changes nothing");

        (await FlagAsync(a, "user-1")).Should().BeFalse("no release, no 60 s wait on this instance");
        (await FlagAsync(b, "user-1")).Should().BeTrue("another company is not affected");
        (await LedgerAsync(a)).Should().Be(1);
        (await LedgerAsync(b)).Should().Be(0);

        var overrides = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/flags/tenants/{a}", null));
        overrides.GetProperty("items").EnumerateArray().Single().GetProperty("enabled").GetBoolean().Should().BeFalse();

        (await SetAsync($"/api/v1/flags/tenants/{a}/{Banner}", new { enabled = (bool?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await FlagAsync(a, "user-1")).Should().BeTrue("the override is removed");
        (await LedgerAsync(a)).Should().Be(2);
    }

    [Fact]
    public async Task A_user_override_beats_the_company_s()
    {
        var tenant = await ProvisionAsync();
        await SetAsync($"/api/v1/flags/tenants/{tenant}/{Banner}", new { enabled = false });
        await SetAsync($"/api/v1/flags/tenants/{tenant}/{Banner}", new { enabled = true, userSubject = "pilot-user" });

        (await FlagAsync(tenant, "pilot-user")).Should().BeTrue();
        (await FlagAsync(tenant, "someone-else")).Should().BeFalse();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-009")]
    public async Task A_platform_kill_switch_applies_everywhere_and_its_history_cannot_be_rewritten()
    {
        var tenant = await ProvisionAsync();
        var withOverride = await ProvisionAsync();
        await SetAsync($"/api/v1/flags/tenants/{withOverride}/{Banner}", new { enabled = true });

        try
        {
            (await SetAsync($"/api/v1/flags/{Banner}", new { enabled = false })).StatusCode.Should().Be(HttpStatusCode.OK);

            (await FlagAsync(tenant, "user-1")).Should().BeFalse("the platform value applies at once");
            (await FlagAsync(withOverride, "user-1")).Should().BeTrue("a company override is more specific");
            var catalog = await Json(await SendAsync(HttpMethod.Get, "/api/v1/flags/catalog", null));
            catalog.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == Banner).GetProperty("platform").GetBoolean().Should().BeFalse();
            (await ScalarAsync(host.Database.Admin, $"SELECT count(*) FROM flags.platform_flag_changes WHERE key = '{Banner}' AND after = false")).Should().BeGreaterThanOrEqualTo(1);

            var rewrite = async () => await ScalarAsync(host.Database.App, "UPDATE flags.platform_flag_changes SET after = true");
            await rewrite.Should().ThrowAsync<PostgresException>();
            var erase = async () => await ScalarAsync(host.Database.Owner, "DELETE FROM flags.platform_flag_changes");
            (await erase.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        }
        finally
        {
            await SetAsync($"/api/v1/flags/{Banner}", new { enabled = (bool?)null });
        }

        (await FlagAsync(tenant, "user-1")).Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_flags_and_companies_are_refused_and_only_operators_change_flags()
    {
        var tenant = await ProvisionAsync();

        var unknown = await SetAsync($"/api/v1/flags/tenants/{tenant}/portal.usage-baner", new { enabled = true });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(unknown)).GetProperty("code").GetString().Should().Be("flags.flag_unknown");
        var platformUser = await SetAsync($"/api/v1/flags/{Banner}", new { enabled = true, userSubject = "x" });
        (await Json(platformUser)).GetProperty("code").GetString().Should().Be("flags.subject_not_allowed");
        var missing = await SetAsync($"/api/v1/flags/tenants/{Guid.CreateVersion7()}/{Banner}", new { enabled = true });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await SendAsync(HttpMethod.Put, $"/api/v1/flags/{Banner}", TestTokens.ForTenant(tenant), new { enabled = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a company user is not an operator");
        (await SendAsync(HttpMethod.Get, "/api/v1/flags", TestTokens.ForPlatformOperator())).StatusCode.Should().NotBe(HttpStatusCode.OK, "the caller's flags need a company");
    }

    private async Task<bool> FlagAsync(Guid tenant, string subject) =>
        (await Json(await SendAsync(HttpMethod.Get, "/api/v1/flags", TestTokens.ForTenant(tenant, subject)))).GetProperty("flags").GetProperty(Banner).GetBoolean();

    private Task<HttpResponseMessage> SetAsync(string path, object body) => SendAsync(HttpMethod.Put, path, null, body);

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"f-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", null, new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
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

    private Task<long> LedgerAsync(Guid tenant) =>
        ScalarAsync(host.Database.Admin, $"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'flags.tenant_flag_changed.v1'");

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }
}
