using Karry.Modules.Billing.Domain;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Billing.Tests;

/// <summary>Plan + add-ons + overrides → entitlements (US-P1-BIL-03, TC-P1-BIL-001 table-driven).</summary>
[Trait("Story", "US-P1-BIL-03")]
[Trait("TestCase", "TC-P1-BIL-001")]
public sealed class EntitlementRulesTests
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));

    private readonly Plan professional;

    public EntitlementRulesTests() => professional = Plan.Publish(clock, "professional", null, "Professional",
        [Features.FieldOperations, Features.Maintenance],
        new Dictionary<string, int> { [Limits.OfficeUsers] = 15, [Limits.AssetsPerSite] = 50, [Limits.EdgeAgentsPerSite] = 0 }, [], actor: null).Value;

    [Fact]
    public void The_plan_alone_gives_its_features_and_limits()
    {
        var entitlements = Resolve();

        entitlements.Features.Should().BeEquivalentTo([Features.FieldOperations, Features.Maintenance]);
        entitlements.TotalLimit(Limits.OfficeUsers).Should().Be(15);
        entitlements.TotalLimit(Limits.RetentionYears).Should().BeNull("a limit the plan leaves out is unlimited");
        entitlements.Status.Should().Be("trial");
    }

    [Theory]
    [InlineData(AddOns.AiCopilot, Features.AiCopilot)]
    [InlineData(AddOns.EdgeAgent, Features.EdgeAgent)]
    [InlineData(AddOns.Telematics, Features.Telematics)]
    public void An_add_on_adds_its_feature(string addOn, string feature) =>
        Resolve(addOns: new Dictionary<string, int> { [addOn] = 1 }).Includes(feature).Should().BeTrue();

    [Fact]
    public void Per_site_limits_scale_with_sites_and_extra_assets_and_edge_agents_with_their_add_on()
    {
        var entitlements = Resolve(sites: 3, extraAssets: 10, addOns: new Dictionary<string, int> { [AddOns.EdgeAgent] = 2 });

        entitlements.TotalLimit(Limits.AssetsPerSite).Should().Be((50 * 3) + 10);
        entitlements.TotalLimit(Limits.EdgeAgentsPerSite).Should().Be(2 * 3, "two edge agent licences per site on three sites");
        entitlements.TotalLimit(Limits.OfficeUsers).Should().Be(15, "office seats are per tenant");
    }

    [Fact]
    public void Sales_overrides_add_features_and_replace_limits()
    {
        var entitlements = Resolve(extraFeatures: [Features.Plant], limitOverrides: new Dictionary<string, int> { [Limits.OfficeUsers] = 25 });

        entitlements.Includes(Features.Plant).Should().BeTrue();
        entitlements.TotalLimit(Limits.OfficeUsers).Should().Be(25);
    }

    [Fact]
    public void A_tenant_without_a_subscription_gets_the_trial_terms_of_the_plan()
    {
        var entitlements = EntitlementRules.Resolve(professional, subscription: null);

        entitlements.PlanCode.Should().Be("professional");
        entitlements.Status.Should().Be("trial");
        entitlements.Sites.Should().Be(1);
    }

    private TenantEntitlements Resolve(int sites = 1, int extraAssets = 0, Dictionary<string, int>? addOns = null, string[]? extraFeatures = null,
        Dictionary<string, int>? limitOverrides = null)
    {
        var subscription = Subscription.StartTrial(clock, new TenantId(Guid.CreateVersion7()), professional, actor: null);
        subscription.ChangeTerms(clock, SubscriptionTerms.Of(professional, BillingPeriod.Monthly, sites, extraAssets, addOns, extraFeatures, limitOverrides), "op");
        return EntitlementRules.Resolve(professional, subscription);
    }
}
