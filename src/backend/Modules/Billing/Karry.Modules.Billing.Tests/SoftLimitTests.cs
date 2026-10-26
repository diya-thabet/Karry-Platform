using Karry.SharedKernel.Entitlements;

namespace Karry.Modules.Billing.Tests;

/// <summary>The soft-limit rule (US-P1-BIL-04 AC2, ADR-0023): banner above the limit, creation stops at limit + 10 % grace.</summary>
[Trait("Story", "US-P1-BIL-04")]
public sealed class SoftLimitTests
{
    [Theory]
    [InlineData(20, 22)]
    [InlineData(15, 17)]
    [InlineData(100, 110)]
    [InlineData(3, 4)]
    [InlineData(1, 2)]
    [InlineData(0, 0)]
    public void The_ceiling_is_the_limit_plus_10_percent_rounded_up(long limit, long ceiling) =>
        SoftLimit.Ceiling(limit).Should().Be(ceiling);

    [Theory]
    [InlineData(0L, 20L, "within")]
    [InlineData(20L, 20L, "within")]
    [InlineData(21L, 20L, "over")]
    [InlineData(22L, 20L, "blocked")]
    [InlineData(23L, 20L, "blocked")]
    [InlineData(5000L, null, "within")]
    public void The_state_follows_the_usage(long usage, long? limit, string state) =>
        SoftLimit.State(usage, limit).Should().Be(state);

    [Theory]
    [InlineData(20L, 20L, true)]
    [InlineData(21L, 20L, true)]
    [InlineData(22L, 20L, false)]
    [InlineData(105L, 100L, true)]
    [InlineData(115L, 100L, false)]
    [InlineData(1_000_000L, null, true)]
    public void One_more_is_allowed_until_the_ceiling(long usage, long? limit, bool allowed) =>
        SoftLimit.AllowsOneMore(usage, limit).Should().Be(allowed);

    [Fact]
    public void Each_limited_metric_maps_to_its_plan_limit()
    {
        Metrics.LimitOf(Metrics.OfficeUsers).Should().Be(Limits.OfficeUsers);
        Metrics.LimitOf(Metrics.Assets).Should().Be(Limits.AssetsPerSite);
        Metrics.LimitOf(Metrics.Sites).Should().BeNull("sites have no plan limit: they are bought");
        Metrics.LimitOf(Metrics.StorageBytes).Should().BeNull();
    }

    [Fact]
    public void Sites_are_held_against_the_sites_bought_and_assets_against_the_plan_per_site()
    {
        var entitlements = new TenantEntitlements("professional", 1, "active", null, new HashSet<string>(),
            new Dictionary<string, int> { [Limits.AssetsPerSite] = 50, [Limits.OfficeUsers] = 15 }, Sites: 3, ExtraAssets: 10);

        entitlements.MetricLimit(Metrics.Sites).Should().Be(3, "ADR-0024: the sites bought");
        entitlements.MetricLimit(Metrics.Assets).Should().Be(160);
        entitlements.MetricLimit(Metrics.OfficeUsers).Should().Be(15);
        entitlements.MetricLimit(Metrics.StorageBytes).Should().BeNull();
    }
}
