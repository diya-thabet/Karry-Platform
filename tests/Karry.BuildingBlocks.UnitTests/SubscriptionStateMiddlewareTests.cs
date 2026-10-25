using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>The subscription-state table of ADR-0023 (US-P1-BIL-05 AC1–AC3) and its middleware.</summary>
[Trait("Story", "US-P1-BIL-05")]
public sealed class SubscriptionStateMiddlewareTests : IAsyncLifetime
{
    private static readonly Guid Tenant = Guid.Parse("01970000-0000-7000-8000-00000000000c");

    private string status = "active";
    private WebApplication? app;
    private HttpClient? client;
    private int handled;

    public static TheoryData<string, SubscriptionAccess, bool, string?> Table()
    {
        const string R = SubscriptionRules.RestrictedCode, S = SubscriptionRules.SuspendedCode, I = SubscriptionRules.InactiveCode;
        var data = new TheoryData<string, SubscriptionAccess, bool, string?>();
        foreach (var open in new[] { "trial", "active", "past_due" })
        {
            foreach (var access in Enum.GetValues<SubscriptionAccess>())
            {
                data.Add(open, access, true, null);
                data.Add(open, access, false, null);
            }
        }

        // state, access, read, refusal
        (string, SubscriptionAccess, bool, string?)[] rows =
        [
            ("restricted", SubscriptionAccess.Office, true, null), ("restricted", SubscriptionAccess.Office, false, R),
            ("restricted", SubscriptionAccess.Account, false, R), ("restricted", SubscriptionAccess.FieldCapture, false, null),
            ("restricted", SubscriptionAccess.Billing, false, null), ("restricted", SubscriptionAccess.Export, false, null),
            ("suspended", SubscriptionAccess.Office, true, null), ("suspended", SubscriptionAccess.FieldCapture, true, null),
            ("suspended", SubscriptionAccess.Office, false, S), ("suspended", SubscriptionAccess.FieldCapture, false, S),
            ("suspended", SubscriptionAccess.Account, false, S), ("suspended", SubscriptionAccess.Billing, false, null),
            ("suspended", SubscriptionAccess.Export, false, null),
        ];
        foreach (var (state, access, read, refusal) in rows)
        {
            data.Add(state, access, read, refusal);
        }

        foreach (var ended in new[] { "cancelled", "expired", "deleted", "something-new" })
        {
            data.Add(ended, SubscriptionAccess.Office, true, I);
            data.Add(ended, SubscriptionAccess.Office, false, I);
            data.Add(ended, SubscriptionAccess.FieldCapture, true, I);
            data.Add(ended, SubscriptionAccess.FieldCapture, false, I);
            data.Add(ended, SubscriptionAccess.Account, true, null);
            data.Add(ended, SubscriptionAccess.Account, false, I);
            data.Add(ended, SubscriptionAccess.Billing, true, null);
            data.Add(ended, SubscriptionAccess.Billing, false, null);
            data.Add(ended, SubscriptionAccess.Export, true, null);
            data.Add(ended, SubscriptionAccess.Export, false, null);
        }

        return data;
    }

    public async ValueTask InitializeAsync() => await StartAsync(withResolver: true);

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Each_state_allows_only_its_work(string state, SubscriptionAccess access, bool read, string? refusal) =>
        SubscriptionRules.Refusal(state, access, read).Should().Be(refusal);

    [Fact]
    [Trait("TestCase", "TC-P1-BIL-005")]
    public async Task A_refused_write_answers_the_state_code_and_never_runs_the_endpoint()
    {
        status = "restricted";

        var refused = await client!.PostAsync("/t/office", null, TestContext.Current.CancellationToken);
        var field = await client.PostAsync("/t/field", null, TestContext.Current.CancellationToken);
        var read = await client.GetAsync("/t/office", TestContext.Current.CancellationToken);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().Should().Be(SubscriptionRules.RestrictedCode);
        problem.GetProperty("subscriptionStatus").GetString().Should().Be("restricted");
        field.StatusCode.Should().Be(HttpStatusCode.OK, "field capture continues while restricted");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        handled.Should().Be(2, "only the field capture and the read ran");
    }

    [Fact]
    public async Task Platform_endpoints_are_not_subject_to_a_tenant_s_state()
    {
        status = "cancelled";

        (await client!.PostAsync("/platform", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_a_resolver_nothing_is_enforced()
    {
        await DisposeAsync();
        await StartAsync(withResolver: false);
        status = "cancelled";

        (await client!.PostAsync("/t/office", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task StartAsync(bool withResolver)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryAuthorization();
        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddSingleton<IAccessResolver>(new NoAssignments());
        if (withResolver)
        {
            builder.Services.AddSingleton<IEntitlementResolver>(new Resolver(() => status));
        }

        app = builder.Build();
        app.UseKarryApiConventions();
        app.Use(SignedIn);
        app.UseKarryAuthorization();

        var tenantEndpoints = app.MapGroup("/t").WithMetadata(new TenantRequired());
        tenantEndpoints.MapGet("/office", Handle).AllowAnyTenantMember();
        tenantEndpoints.MapPost("/office", Handle).AllowAnyTenantMember();
        tenantEndpoints.MapPost("/field", Handle).AllowAnyTenantMember().FieldCapture();
        app.MapPost("/platform", Handle).AllowWithoutTenant().AllowAnonymous();

        await app.StartAsync();
        client = app.GetTestClient();
    }

    private IResult Handle()
    {
        handled++;
        return Results.Ok();
    }

    private static Task SignedIn(HttpContext context, RequestDelegate next)
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test"));
        if (!context.Request.Path.StartsWithSegments("/platform"))
        {
            context.RequestServices.GetRequiredService<ITenantContext>().Set(new TenantId(Tenant));
        }

        return next(context);
    }

    private sealed class Resolver(Func<string> status) : IEntitlementResolver
    {
        public ValueTask<TenantEntitlements> ResolveAsync(TenantId tenant, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new TenantEntitlements("professional", 1, status(), null, new HashSet<string>(), new Dictionary<string, int>(), 1, 0));
    }

    private sealed class NoAssignments : IAccessResolver
    {
        public ValueTask<IReadOnlyList<AccessAssignment>> AssignmentsAsync(TenantId tenant, string subject, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AccessAssignment>>([]);
    }
}
