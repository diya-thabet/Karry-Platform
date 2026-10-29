using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.Testing;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// The operator console's tenant list (US-P1-OPS-03 AC1, ADR-0029 §3) on the real host: every module answers for each
/// tenant under that tenant's context (sites, machines, office users, plan, state, MRR, last activity).
/// </summary>
[Trait("Story", "US-P1-OPS-03")]
public sealed class OperatorConsoleTests(SyncHost host) : IClassFixture<SyncHost>
{
    private const string Tenants = "/api/v1/tenancy/tenants";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_tenant_is_listed_with_its_own_counts_plan_state_mrr_and_last_activity()
    {
        var company = await host.CompanyAsync("ops");
        await host.StartTrialAsync(company);
        await host.StartTrialAsync(host.B);
        var operatorToken = TestTokens.ForPlatformOperator();
        host.Clock.Advance(TimeSpan.FromHours(1));
        var activatedAt = host.Clock.GetUtcNow();
        var terms = await host.SendAsync(HttpMethod.Put, $"/api/v1/billing/tenants/{company.Id}/subscription", operatorToken,
            new { planCode = "professional", period = "annual", sites = 2, extraAssets = 3 });
        terms.IsSuccessStatusCode.Should().BeTrue(await terms.Content.ReadAsStringAsync(Ct));
        var activated = await host.SendAsync(HttpMethod.Post, $"/api/v1/billing/tenants/{company.Id}/subscription/actions", operatorToken, new { action = "activate" });
        activated.IsSuccessStatusCode.Should().BeTrue(await activated.Content.ReadAsStringAsync(Ct));

        var response = await host.SendAsync(HttpMethod.Get, Tenants, operatorToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var row = Row(list, company.Id);
        row.GetProperty("slug").GetString().Should().Be(company.Slug);
        row.GetProperty("sites").GetInt64().Should().Be(2);
        row.GetProperty("assets").GetInt64().Should().Be(3);
        row.GetProperty("officeUsers").GetInt64().Should().Be(1);
        row.GetProperty("planCode").GetString().Should().Be("professional");
        row.GetProperty("planVersion").GetInt32().Should().Be(1);
        row.GetProperty("status").GetString().Should().Be("active");
        row.GetProperty("mrr").GetDecimal().Should().Be(995.83m, "(2 × 5900 + 3 × 50) / 12 on the seeded annual price");
        row.GetProperty("mrrCurrency").GetString().Should().Be("EUR");
        row.GetProperty("lastActivity").GetDateTimeOffset().Should().BeCloseTo(activatedAt, TimeSpan.FromMilliseconds(1), "the activation is the newest ledger entry");
        row.GetProperty("complete").GetBoolean().Should().BeTrue();

        var other = Row(list, host.B.Id);
        other.GetProperty("status").GetString().Should().Be("trial");
        other.GetProperty("mrr").GetDecimal().Should().Be(0m, "a trial is not billed");
        other.GetProperty("assets").GetInt64().Should().Be(3);
        var euros = list.GetProperty("mrr").EnumerateArray().Single(t => t.GetProperty("currency").GetString() == "EUR");
        euros.GetProperty("amount").GetDecimal().Should().BeGreaterThanOrEqualTo(995.83m);
        list.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task Only_platform_operators_get_the_list()
    {
        (await host.SendAsync(HttpMethod.Get, Tenants, host.Admin(host.A))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await host.Client.GetAsync(new Uri(Tenants, UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static JsonElement Row(JsonElement list, Guid tenant) =>
        list.GetProperty("tenants").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == tenant);
}
