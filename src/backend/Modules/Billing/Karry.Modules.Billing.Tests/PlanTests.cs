using Karry.Modules.Billing.Domain;
using Karry.SharedKernel.Entitlements;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Billing.Tests;

/// <summary>Plan versions as data (US-P1-BIL-01 AC1–AC2, ADR-0023): validation of codes, features, limits and prices.</summary>
[Trait("Story", "US-P1-BIL-01")]
public sealed class PlanTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));

    private static readonly PlanPrice Monthly = new("EUR", BillingPeriod.Monthly, 249m, 6m);

    [Fact]
    public void A_new_plan_is_version_1_with_sorted_distinct_features()
    {
        var plan = Publish("starter", latest: null, features: [Features.Fuel, Features.FieldOperations, Features.Fuel]).Value;

        plan.Version.Should().Be(1);
        plan.Features.Should().Equal(Features.FieldOperations, Features.Fuel);
        plan.Includes(Features.Fuel).Should().BeTrue();
        plan.Includes(Features.Plant).Should().BeFalse();
        plan.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<PlanPublished>().Which.Version.Should().Be(1);
    }

    [Fact]
    public void Publishing_again_makes_the_next_version_and_leaves_the_previous_one_as_it_was()
    {
        var first = Publish("starter", latest: null).Value;

        var second = Publish("starter", first, limits: new Dictionary<string, int> { [Limits.OfficeUsers] = 8 }).Value;

        second.Version.Should().Be(2);
        second.Id.Should().NotBe(first.Id);
        first.Limits.Should().ContainKey(Limits.OfficeUsers).WhoseValue.Should().Be(5, "a published version never changes");
        second.Limits[Limits.OfficeUsers].Should().Be(8);
    }

    [Theory]
    [InlineData("Starter")]
    [InlineData("s")]
    [InlineData("1starter")]
    [InlineData("star ter")]
    public void A_plan_code_is_lowercase_letters_digits_and_hyphens(string code) =>
        Publish(code, latest: null).Error!.Code.Should().Be("billing.plan_code_invalid");

    [Fact]
    public void Unknown_features_and_limits_are_refused()
    {
        Publish("starter", latest: null, features: ["teleportation"]).Error!.Code.Should().Be("billing.feature_unknown");
        Publish("starter", latest: null, limits: new Dictionary<string, int> { ["moons"] = 1 }).Error!.Code.Should().Be("billing.limit_unknown");
        Publish("starter", latest: null, limits: new Dictionary<string, int> { [Limits.OfficeUsers] = -1 }).Error!.Code.Should().Be("billing.limit_invalid");
    }

    [Fact]
    public void Prices_are_in_a_supported_currency_with_its_minor_units_and_one_per_currency_and_period()
    {
        Publish("starter", latest: null, prices: [Monthly with { Currency = "XXX" }]).Error!.Code.Should().Be("billing.currency_unsupported");
        Publish("starter", latest: null, prices: [Monthly with { PerSite = 249.001m }]).Error!.Code.Should().Be("billing.price_invalid");
        Publish("starter", latest: null, prices: [Monthly with { ExtraAsset = -1m }]).Error!.Code.Should().Be("billing.price_invalid");
        Publish("starter", latest: null, prices: [Monthly, Monthly with { PerSite = 300m }]).Error!.Code.Should().Be("billing.price_duplicate");
        Publish("starter", latest: null, prices: [Monthly, Monthly with { Period = BillingPeriod.Annual, PerSite = 2490m }]).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_plan_quoted_by_sales_has_no_prices_and_no_limits()
    {
        var plan = Publish("enterprise", latest: null, limits: new Dictionary<string, int>(), prices: []).Value;

        plan.Prices.Should().BeEmpty();
        plan.Limits.Should().BeEmpty("a limit left out is unlimited");
    }

    [Fact]
    public void The_latest_version_must_belong_to_the_same_plan()
    {
        var starter = Publish("starter", latest: null).Value;

        var act = () => Publish("business", starter);

        act.Should().Throw<ArgumentException>();
    }

    private static SharedKernel.Results.Result<Plan> Publish(string code, Plan? latest, string[]? features = null,
        Dictionary<string, int>? limits = null, PlanPrice[]? prices = null) =>
        Plan.Publish(Clock, code, latest, "Starter", features ?? [Features.FieldOperations],
            limits ?? new Dictionary<string, int> { [Limits.OfficeUsers] = 5 }, prices ?? [Monthly], actor: "operator-1");
}
