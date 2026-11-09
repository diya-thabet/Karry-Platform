using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Billing.Application;
using Karry.Modules.Tenancy.Infrastructure;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Karry.Modules.Billing.Tests;

/// <summary>
/// Subscriptions over HTTP (US-P1-BIL-02, US-P1-TEN-01 AC1): a provisioned tenant starts a trial, operators move its
/// state and record invoices in the tenant's ledger, the lifecycle job applies the time rules, and the tenant sees
/// its own subscription only.
/// </summary>
[Trait("Story", "US-P1-BIL-02")]
public sealed class SubscriptionApiTests(BillingHost host) : IClassFixture<BillingHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Story", "US-P1-TEN-01")]
    public async Task A_provisioned_tenant_starts_a_30_day_trial_of_Professional_recorded_in_its_ledger()
    {
        var tenant = await ProvisionAsync();

        var subscription = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/billing/tenants/{tenant}/subscription"));

        subscription.GetProperty("status").GetString().Should().Be("trial");
        subscription.GetProperty("planCode").GetString().Should().Be("professional");
        subscription.GetProperty("sites").GetInt32().Should().Be(1);
        subscription.GetProperty("trialEndsAt").GetDateTimeOffset().Should().Be(subscription.GetProperty("statusSince").GetDateTimeOffset().AddDays(30));
        (await LedgerCountAsync(tenant, "billing.subscription_started.v1")).Should().Be(1);

        await DispatchAsync();
        (await CountAsync($"SELECT count(*) FROM billing.subscriptions WHERE tenant_id = '{tenant}'")).Should().Be(1, "a second delivery keeps the one subscription");
    }

    [Fact]
    public async Task Operators_move_the_state_through_the_table_only()
    {
        var tenant = await ProvisionAsync();

        (await StatusAfterAsync(tenant, "activate")).Should().Be("active");
        (await StatusAfterAsync(tenant, "missed-payment")).Should().Be("past_due");

        var refused = await ActAsync(tenant, "activate");
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(refused)).GetProperty("code").GetString().Should().Be("billing.transition_invalid");
        (await ActAsync(tenant, "teleport")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LedgerCountAsync(tenant, "billing.subscription_status_changed.v1")).Should().Be(2);
    }

    [Fact]
    public async Task Paying_an_invoice_brings_a_past_due_subscription_back_in_the_same_change()
    {
        var tenant = await ProvisionAsync();
        await ActAsync(tenant, "activate");
        await ActAsync(tenant, "missed-payment");

        var recorded = await SendAsync(HttpMethod.Post, $"/api/v1/billing/tenants/{tenant}/invoices",
            body: new { number = "K-2026-001", amount = 590.00m, currency = "EUR", issuedOn = "2026-10-01", dueOn = "2026-10-31" });
        recorded.StatusCode.Should().Be(HttpStatusCode.OK);
        var invoice = (await Json(recorded)).GetProperty("id").GetGuid();

        var duplicate = await SendAsync(HttpMethod.Post, $"/api/v1/billing/tenants/{tenant}/invoices",
            body: new { number = "K-2026-001", amount = 10m, currency = "EUR", issuedOn = "2026-10-01", dueOn = "2026-10-31" });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var paid = await SendAsync(HttpMethod.Post, $"/api/v1/billing/tenants/{tenant}/invoices/{invoice}/payment", body: new { paidOn = "2026-10-20" });
        paid.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(paid)).GetProperty("paidOn").GetString().Should().Be("2026-10-20");
        (await Status(tenant)).Should().Be("active");

        var invoices = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/billing/tenants/{tenant}/invoices"));
        invoices.GetProperty("items").EnumerateArray().Single().GetProperty("amount").GetDecimal().Should().Be(590.00m);
        (await LedgerCountAsync(tenant, "billing.invoice_paid.v1")).Should().Be(1);
    }

    [Fact]
    public async Task The_lifecycle_job_expires_a_trial_that_ended()
    {
        var tenant = await ProvisionAsync();

        host.Clock.Advance(TimeSpan.FromDays(31));
        var run = await host.Services.GetRequiredService<AdvanceSubscriptions>().RunAsync(Ct);

        run.Failed.Should().Be(0);
        run.Changed.Should().BeGreaterThanOrEqualTo(1);
        (await Status(tenant)).Should().Be("expired");
        (await host.Services.GetRequiredService<AdvanceSubscriptions>().RunAsync(Ct)).Changed.Should().Be(0, "nothing more is due");
    }

    [Fact]
    public async Task Terms_change_with_a_plan_version_and_unknown_plans_are_refused()
    {
        var tenant = await ProvisionAsync();

        var changed = await SendAsync(HttpMethod.Put, $"/api/v1/billing/tenants/{tenant}/subscription",
            body: new { planCode = "business", period = "annual", sites = 3, extraAssets = 10, addOns = new Dictionary<string, int> { ["telematics"] = 1 } });

        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(changed);
        body.GetProperty("planCode").GetString().Should().Be("business");
        body.GetProperty("period").GetString().Should().Be("annual");
        body.GetProperty("sites").GetInt32().Should().Be(3);
        (await LedgerCountAsync(tenant, "billing.subscription_terms_changed.v1")).Should().Be(1);

        var unknown = await SendAsync(HttpMethod.Put, $"/api/v1/billing/tenants/{tenant}/subscription",
            body: new { planCode = "platinum", period = "monthly", sites = 1, extraAssets = 0 });
        (await Json(unknown)).GetProperty("code").GetString().Should().Be("billing.plan_unknown");
    }

    [Fact]
    public async Task A_tenant_sees_its_own_subscription_and_operators_address_only_existing_tenants()
    {
        var tenant = await ProvisionAsync();

        var own = await SendAsync(HttpMethod.Get, "/api/v1/billing/subscription", TestTokens.ForTenant(tenant));
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(own)).GetProperty("status").GetString().Should().Be("trial");
        (await SendAsync(HttpMethod.Get, "/api/v1/billing/invoices", TestTokens.ForTenant(tenant))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendAsync(HttpMethod.Get, $"/api/v1/billing/tenants/{tenant}/subscription", TestTokens.ForTenant(tenant)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a tenant user is not an operator");
        var missing = await SendAsync(HttpMethod.Get, $"/api/v1/billing/tenants/{Guid.CreateVersion7()}/subscription");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(missing)).GetProperty("code").GetString().Should().Be("billing.tenant_not_found");
    }

    [Fact]
    [Trait("Story", "US-P1-BIL-03")]
    [Trait("TestCase", "TC-P1-BIL-003")]
    public async Task Moving_a_tenant_from_Professional_to_Starter_locks_its_maintenance_features_at_once()
    {
        var tenant = await ProvisionAsync();
        var token = TestTokens.ForTenant(tenant);

        (await SendAsync(HttpMethod.Get, "/api/v1/probe/work-orders", token)).StatusCode.Should().Be(HttpStatusCode.OK, "the trial is Professional");
        var before = await Json(await SendAsync(HttpMethod.Get, "/api/v1/billing/entitlements", token));
        before.GetProperty("planCode").GetString().Should().Be("professional");
        before.GetProperty("features").EnumerateArray().Select(f => f.GetString()).Should().Contain("maintenance");
        before.GetProperty("limits").GetProperty("office-users").GetInt64().Should().Be(15);

        (await SendAsync(HttpMethod.Put, $"/api/v1/billing/tenants/{tenant}/subscription",
            body: new { planCode = "starter", period = "monthly", sites = 2, extraAssets = 5 })).StatusCode.Should().Be(HttpStatusCode.OK);

        var locked = await SendAsync(HttpMethod.Get, "/api/v1/probe/work-orders", token);
        locked.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the cache is forgotten on the instance that made the change");
        var problem = await locked.Content.ReadAsStringAsync(Ct);
        problem.Should().Contain("billing.feature_not_in_plan").And.Contain("maintenance").And.NotContain("WO-1", "no data in the refusal");

        var after = await Json(await SendAsync(HttpMethod.Get, "/api/v1/billing/entitlements", token));
        after.GetProperty("planCode").GetString().Should().Be("starter");
        after.GetProperty("limits").GetProperty("assets-per-site").GetInt64().Should().Be((20 * 2) + 5, "the tenant-wide total");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task Terms_changed_from_an_old_version_are_refused_and_the_newer_change_is_kept()
    {
        // Hardening plan H-C1: two operators opened the same subscription; the second to save must not overwrite the first.
        var tenant = await ProvisionAsync();
        var opened = await SendAsync(HttpMethod.Get, $"/api/v1/billing/tenants/{tenant}/subscription");
        var version = opened.Headers.ETag!.Tag;
        (await Json(opened)).GetProperty("version").GetString().Should().Be(version, "body and header carry the same version");

        var first = await SendAsync(HttpMethod.Put, $"/api/v1/billing/tenants/{tenant}/subscription", body: Terms(sites: 3), ifMatch: version);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag!.Tag.Should().NotBe(version);

        var second = await SendAsync(HttpMethod.Put, $"/api/v1/billing/tenants/{tenant}/subscription", body: Terms(sites: 5), ifMatch: version);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(second)).GetProperty("code").GetString().Should().Be("request.version_conflict");

        (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/billing/tenants/{tenant}/subscription"))).GetProperty("sites").GetInt32().Should().Be(3);
        (await LedgerCountAsync(tenant, "billing.subscription_terms_changed.v1")).Should().Be(1, "the refused change left no trace");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task Actions_at_the_same_moment_answer_200_409_or_422_never_500()
    {
        // The subscription always had a row version, but nobody caught its conflict: two operator actions at once
        // (or an action during the hourly run) answered 500.
        // A 200 must also report what was saved: one version is one state, whoever answers it.
        var tenant = await ProvisionAsync();
        var activated = await ActAsync(tenant, "activate");
        activated.StatusCode.Should().Be(HttpStatusCode.OK);
        var states = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { [activated.Headers.ETag!.Tag] = ["active"] };

        var actions = await Task.WhenAll(Enumerable.Range(0, 8).Select(n => ActAsync(tenant, n % 2 == 0 ? "missed-payment" : "payment")));

        // 422: the move is not allowed from the state that request loaded (a missed payment on a PastDue subscription).
        actions.Should().OnlyContain(a => a.StatusCode == HttpStatusCode.OK || a.StatusCode == HttpStatusCode.Conflict || a.StatusCode == HttpStatusCode.UnprocessableEntity);
        foreach (var action in actions.Where(a => a.StatusCode != HttpStatusCode.UnprocessableEntity))
        {
            var body = await Json(action);
            if (action.StatusCode == HttpStatusCode.Conflict)
            {
                body.GetProperty("code").GetString().Should().Be("request.version_conflict");
                continue;
            }

            var version = body.GetProperty("version").GetString()!;
            states.TryAdd(version, []);
            states[version].Add(body.GetProperty("status").GetString()!);
        }

        states.Should().OnlyContain(s => s.Value.Count == 1, "a 200 for a save that was refused would report a state no version has");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task The_lifecycle_run_never_overwrites_a_payment_recorded_after_it_loaded_the_subscription()
    {
        // The run loaded a PastDue subscription and would restrict it; meanwhile an operator recorded the payment. The
        // run's save is refused (false, not an exception) and the company stays Active.
        var tenant = await ProvisionAsync();
        (await ActAsync(tenant, "activate")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ActAsync(tenant, "missed-payment")).StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var store = scope.ServiceProvider.GetRequiredService<ISubscriptionStore>();
        var stale = (await store.FindAsync(Ct))!;

        (await ActAsync(tenant, "payment")).StatusCode.Should().Be(HttpStatusCode.OK);

        var later = new FakeTimeProvider(host.Clock.GetUtcNow().AddDays(20));
        stale.Advance(later).Should().BeTrue("from what it loaded, the run would restrict the company");
        (await store.TrySaveAsync(Ct)).Should().BeFalse();
        (await Status(tenant)).Should().Be("active");
    }

    [Theory]
    [Trait("Story", "P1.18a")]
    [InlineData("4711")]
    [InlineData("\"abc\"")]
    public async Task A_malformed_if_match_is_refused(string ifMatch)
    {
        var tenant = await ProvisionAsync();

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/billing/tenants/{tenant}/subscription", body: Terms(sites: 3), ifMatch: ifMatch);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be("request.if_match_invalid");
    }

    private static object Terms(int sites) => new { planCode = "business", period = "annual", sites, extraAssets = 0 };

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"t-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", body: new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(response)).GetProperty("id").GetGuid();
        await DispatchAsync();
        return id;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = host.Services.GetRequiredService<OutboxDispatcher<TenancyDbContext>>();
        await dispatcher.DispatchPendingAsync(100, Ct);
        (await dispatcher.PendingAsync(Ct)).Should().Be(0, "every provisioning subscriber succeeds");
    }

    private async Task<string?> StatusAfterAsync(Guid tenant, string action)
    {
        var response = await ActAsync(tenant, action);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(response)).GetProperty("status").GetString();
    }

    private async Task<string?> Status(Guid tenant) =>
        (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/billing/tenants/{tenant}/subscription"))).GetProperty("status").GetString();

    private Task<HttpResponseMessage> ActAsync(Guid tenant, string action) =>
        SendAsync(HttpMethod.Post, $"/api/v1/billing/tenants/{tenant}/subscription/actions", body: new { action });

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token = null, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
            request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForPlatformOperator());
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private Task<long> LedgerCountAsync(Guid tenant, string type) =>
        CountAsync($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = '{type}'");

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
