using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Billing.Api;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Karry.Modules.Billing.Tests;

/// <summary>
/// The Billing module on real PostgreSQL, migrated by the migration runner as <c>karry_owner</c> and used as
/// <c>karry_app</c>, behind the real pipeline (conventions, tenancy, idempotency) with test-issuer tokens.
/// </summary>
public sealed class BillingHost : IAsyncLifetime
{
    private PostgresServer? server;
    private WebApplication? app;

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PostgresServer.StartAsync();
        Database = await server.NewKarryDatabaseAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Karry"] = Database.App });
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryTenancy(builder.Configuration);
        builder.Services.TrustTestIssuer();
        builder.Services.AddIdempotency(Database.App);
        builder.Services.AddModules(builder.Configuration, [new BillingModule(), new Trust.Api.TrustModule()]);
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
        app.UseIdempotency();
        app.MapModules();

        await MigrationRunner.MigrateAsync(app.Services, Database.Owner, CancellationToken.None);
        await app.StartAsync();
        Client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }

        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }
}

/// <summary>The plan catalogue over HTTP (US-P1-BIL-01): seeded plans, new versions, operator-only access.</summary>
[Trait("Story", "US-P1-BIL-01")]
public sealed class PlanApiTests(BillingHost host) : IClassFixture<BillingHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P1-BIL-001")]
    public async Task The_four_plans_of_the_business_model_are_seeded_as_version_1()
    {
        var plans = await ItemsAsync(await SendAsync(HttpMethod.Get, "/api/v1/billing/plans"));

        var byCode = plans.ToDictionary(p => p.GetProperty("code").GetString()!);
        byCode.Keys.Should().Contain(["starter", "professional", "business", "enterprise"]);
        byCode.Values.Where(p => p.GetProperty("code").GetString() is "starter" or "professional" or "business" or "enterprise")
            .Should().OnlyContain(p => p.GetProperty("version").GetInt32() == 1);

        Limit(byCode["starter"], "assets-per-site").Should().Be(20);
        Limit(byCode["starter"], "office-users").Should().Be(5);
        Limit(byCode["professional"], "office-users").Should().Be(15);
        Limit(byCode["business"], "assets-per-site").Should().Be(120);
        Limit(byCode["business"], "edge-agents-per-site").Should().Be(1);
        byCode["enterprise"].GetProperty("limits").EnumerateObject().Should().BeEmpty("Enterprise is unlimited");
        byCode["enterprise"].GetProperty("prices").GetArrayLength().Should().Be(0, "Enterprise is quoted by sales");

        Features(byCode["starter"]).Should().NotContain(["maintenance", "plant"]);
        Features(byCode["professional"]).Should().Contain(["maintenance", "inventory.multi-store"]).And.NotContain("plant");
        Features(byCode["business"]).Should().Contain(["plant", "finance", "ai.copilot", "sso"]).And.NotContain("telematics");
        Features(byCode["enterprise"]).Should().Contain(["telematics", "dedicated-hosting"]);

        var starterMonthly = byCode["starter"].GetProperty("prices").EnumerateArray().Single(p => p.GetProperty("period").GetString() == "monthly");
        starterMonthly.GetProperty("currency").GetString().Should().Be("EUR");
        starterMonthly.GetProperty("perSite").GetDecimal().Should().Be(249m);
        starterMonthly.GetProperty("extraAsset").GetDecimal().Should().Be(6m);
        var businessAnnual = byCode["business"].GetProperty("prices").EnumerateArray().Single(p => p.GetProperty("period").GetString() == "annual");
        businessAnnual.GetProperty("perSite").GetDecimal().Should().Be(9900m, "annual is 10 months");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-BIL-006")]
    public async Task Publishing_a_plan_again_adds_a_version_and_keeps_the_old_one()
    {
        var code = $"p-{Guid.NewGuid():N}"[..20];
        var first = await PublishAsync(code, officeUsers: 5);
        var second = await PublishAsync(code, officeUsers: 8);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(second)).GetProperty("version").GetInt32().Should().Be(2);

        var versions = await ItemsAsync(await SendAsync(HttpMethod.Get, $"/api/v1/billing/plans/{code}"));
        versions.Select(v => v.GetProperty("version").GetInt32()).Should().Equal(2, 1);
        Limit(versions[1], "office-users").Should().Be(5, "version 1 is unchanged");

        var latest = await ItemsAsync(await SendAsync(HttpMethod.Get, "/api/v1/billing/plans"));
        latest.Where(p => p.GetProperty("code").GetString() == code).Should().ContainSingle().Which.GetProperty("version").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_plan_naming_an_unknown_feature_is_refused()
    {
        var response = await PublishAsync($"p-{Guid.NewGuid():N}"[..20], officeUsers: 5, feature: "teleportation");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be("billing.feature_unknown");
    }

    [Fact]
    public async Task An_unknown_plan_code_is_404()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/billing/plans/no-such-plan");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(response)).GetProperty("code").GetString().Should().Be("billing.plan_not_found");
    }

    [Fact]
    public async Task Only_platform_operators_see_and_publish_plans()
    {
        var tenantToken = TestTokens.ForTenant(Guid.CreateVersion7());

        (await SendAsync(HttpMethod.Get, "/api/v1/billing/plans", tenantToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PublishAsync("starter", officeUsers: 99, token: tenantToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/billing/plans", token: string.Empty)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static int Limit(JsonElement plan, string code) => plan.GetProperty("limits").GetProperty(code).GetInt32();

    private static List<string> Features(JsonElement plan) => [.. plan.GetProperty("features").EnumerateArray().Select(f => f.GetString()!)];

    private async Task<HttpResponseMessage> PublishAsync(string code, int officeUsers, string feature = "field-operations", string? token = null)
    {
        var body = new
        {
            code,
            name = "Test plan",
            features = new[] { feature },
            limits = new Dictionary<string, int> { ["office-users"] = officeUsers },
            prices = new[] { new { currency = "EUR", period = "monthly", perSite = 100m, extraAsset = (decimal?)2m } },
        };
        return await SendAsync(HttpMethod.Post, "/api/v1/billing/plans", token, body);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
            request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        }

        token ??= TestTokens.ForPlatformOperator();
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<List<JsonElement>> ItemsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. (await Json(response)).GetProperty("items").EnumerateArray()];
    }
}
