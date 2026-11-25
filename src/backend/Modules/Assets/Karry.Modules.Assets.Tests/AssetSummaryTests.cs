using Karry.Modules.Assets.Contracts;

namespace Karry.Modules.Assets.Tests;

/// <summary>
/// Where a machine was at an instant, as other modules ask it (a pre-start checklist or a fact is placed on the site the
/// machine was on then, not where it is now: a machine moved at noon worked its morning on the old site).
/// </summary>
[Trait("Story", "US-P1-AST-03")]
public sealed class AssetSummaryTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_site_at_an_instant_is_the_last_assignment_started_by_then_and_none_before_the_first()
    {
        var (north, south) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var machine = new AssetSummary(Guid.CreateVersion7(), "PEL-01", "excavator",
            [new AssetSiteAssignment(north, Monday), new AssetSiteAssignment(south, Monday.AddHours(12))]);

        machine.SiteAt(Monday.AddSeconds(-1)).Should().BeNull("not on any site before its first assignment");
        machine.SiteAt(Monday).Should().Be(north, "an assignment counts from its first instant");
        machine.SiteAt(Monday.AddHours(11).AddMinutes(59)).Should().Be(north, "the morning before the move");
        machine.SiteAt(Monday.AddHours(12)).Should().Be(south, "the move counts from its instant");
        machine.SiteAt(Monday.AddDays(30)).Should().Be(south);
        new AssetSummary(Guid.CreateVersion7(), "NEW-01", "excavator", []).SiteAt(Monday).Should().BeNull("a machine never assigned has no site");
    }
}
