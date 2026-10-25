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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token = null, object? body = null)
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
