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

namespace Karry.Modules.Billing.Tests;

/// <summary>
/// The subscription state over HTTP (US-P1-BIL-05): unpaid days restrict office work then everything, payment brings
/// it back at once, and an ended subscription leaves only billing and the person's own account.
/// </summary>
[Trait("Story", "US-P1-BIL-05")]
public sealed class SubscriptionStateApiTests(BillingHost host) : IClassFixture<BillingHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P1-BIL-005")]
    public async Task Restricted_keeps_reads_and_field_capture_suspended_stops_field_capture_and_payment_lifts_both()
    {
        var tenant = await ProvisionAsync();
        var token = TestTokens.ForTenant(tenant);
        await ActAsync(tenant, "activate");
        await ActAsync(tenant, "missed-payment");

        await AfterDaysAsync(15);
        (await StatusAsync(token)).Should().Be("restricted");
        await RefusedAsync(await SendAsync(HttpMethod.Post, "/api/v1/probe/assets", token, new { }), "billing.subscription_restricted", "restricted");
        (await SendAsync(HttpMethod.Post, "/api/v1/probe/field-readings", token, new { })).StatusCode.Should().Be(HttpStatusCode.OK, "field sync is accepted");
        (await SendAsync(HttpMethod.Get, "/api/v1/probe/work-orders", token)).StatusCode.Should().Be(HttpStatusCode.OK, "office reads stay");
        (await SendAsync(HttpMethod.Get, "/api/v1/billing/invoices", token)).StatusCode.Should().Be(HttpStatusCode.OK);

        await AfterDaysAsync(16);
        (await StatusAsync(token)).Should().Be("suspended");
        await RefusedAsync(await SendAsync(HttpMethod.Post, "/api/v1/probe/field-readings", token, new { }), "billing.subscription_suspended", "suspended");
        (await SendAsync(HttpMethod.Get, "/api/v1/probe/work-orders", token)).StatusCode.Should().Be(HttpStatusCode.OK, "everything is still readable");

        var invoice = await Json(await SendAsync(HttpMethod.Post, $"/api/v1/billing/tenants/{tenant}/invoices", null,
            new { number = $"K-{Guid.NewGuid():N}"[..20], amount = 590m, currency = "EUR", issuedOn = "2026-10-01", dueOn = "2026-10-31" }));
        (await SendAsync(HttpMethod.Post, $"/api/v1/billing/tenants/{tenant}/invoices/{invoice.GetProperty("id").GetGuid()}/payment", null,
            new { paidOn = "2026-11-20" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusAsync(token)).Should().Be("active", "the payment forgets the cached state at once");
        (await SendAsync(HttpMethod.Post, "/api/v1/probe/assets", token, new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/v1/probe/field-readings", token, new { })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_ended_subscription_leaves_billing_and_the_person_s_own_account_only()
    {
        var tenant = await ProvisionAsync();
        var token = TestTokens.ForTenant(tenant);
        await ActAsync(tenant, "activate");
        await ActAsync(tenant, "cancel");

        (await StatusAsync(token)).Should().Be("cancelled");
        await RefusedAsync(await SendAsync(HttpMethod.Get, "/api/v1/probe/work-orders", token), "billing.subscription_inactive", "cancelled");
        await RefusedAsync(await SendAsync(HttpMethod.Post, "/api/v1/probe/field-readings", token, new { }), "billing.subscription_inactive", "cancelled");
        (await SendAsync(HttpMethod.Get, "/api/v1/billing/subscription", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/api/v1/billing/usage", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/api/v1/tenancy/current", token)).StatusCode.Should().Be(HttpStatusCode.OK, "the apps still start");
        (await SendAsync(HttpMethod.Get, $"/api/v1/billing/tenants/{tenant}/subscription", null)).StatusCode.Should().Be(HttpStatusCode.OK, "operators are not subject to it");
    }

    private async Task AfterDaysAsync(int days)
    {
        host.Clock.Advance(TimeSpan.FromDays(days));
        (await host.Services.GetRequiredService<AdvanceSubscriptions>().RunAsync(Ct)).Failed.Should().Be(0);
    }

    private static async Task RefusedAsync(HttpResponseMessage response, string code, string state)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await Json(response);
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("subscriptionStatus").GetString().Should().Be(state);
    }

    private async Task<string?> StatusAsync(string token) =>
        (await Json(await SendAsync(HttpMethod.Get, "/api/v1/billing/entitlements", token))).GetProperty("status").GetString();

    private async Task ActAsync(Guid tenant, string action) =>
        (await SendAsync(HttpMethod.Post, $"/api/v1/billing/tenants/{tenant}/subscription/actions", null, new { action }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"s-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", null, new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await Json(response)).GetProperty("id").GetGuid();
        await host.Services.GetRequiredService<OutboxDispatcher<TenancyDbContext>>().DispatchPendingAsync(100, Ct);
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
}
