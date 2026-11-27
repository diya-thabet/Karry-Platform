using Karry.Modules.Sales.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Sales.Tests;

/// <summary>
/// Price resolution and version rules without a database (US-P3-SAL-02 AC2, ADR-0042 §2): the customer's version in
/// force beats the site's, the latest start on or before the date wins, withdrawn versions never price, a version in
/// force never changes, prices are never back-dated.
/// </summary>
[Trait("Story", "US-P3-SAL-02")]
public sealed class PriceRulesTests
{
    private static readonly DateOnly Today = new(2026, 12, 1);
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid Gravel = Guid.NewGuid();
    private static readonly Guid Sand = Guid.NewGuid();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));

    private PriceList Version(int startsInDays, Guid? customer, params PriceLine[] lines) =>
        PriceList.Create(clock, TenantId.From(Guid.NewGuid()), Today, Site, customer, "TND", Today.AddDays(startsInDays), lines, "tester").Value;

    [Fact]
    [Trait("TestCase", "TC-P3-SAL-002")]
    public void The_customer_version_in_force_beats_the_site_version()
    {
        var site = Version(0, null, new PriceLine(Gravel, 30m));
        var own = Version(2, Customer, new PriceLine(Gravel, 27.5m));

        PriceRules.Resolve([site, own], Customer, Gravel, Today.AddDays(1))!.Line.UnitPrice.Should().Be(30m, "the customer's list starts tomorrow");
        var resolved = PriceRules.Resolve([site, own], Customer, Gravel, Today.AddDays(2))!;
        resolved.UnitPrice.Should().Be(Karry.SharedKernel.Monetary.Money.Of(27.5m, "TND"));
        resolved.Source.Should().Be("customer");
        PriceRules.Resolve([site, own], Guid.NewGuid(), Gravel, Today.AddDays(2))!.Source.Should().Be("site", "another customer has no list");
        PriceRules.Resolve([site, own], null, Gravel, Today.AddDays(2))!.Line.UnitPrice.Should().Be(30m, "no customer: the site's price");
    }

    [Fact]
    [Trait("TestCase", "TC-P3-SAL-002")]
    public void The_latest_start_on_or_before_the_date_wins_and_none_means_no_price()
    {
        var first = Version(1, null, new PriceLine(Gravel, 30m));
        var second = Version(10, null, new PriceLine(Gravel, 32m));

        PriceRules.Resolve([first, second], null, Gravel, Today).Should().BeNull("nothing in force yet");
        PriceRules.Resolve([second, first], null, Gravel, Today.AddDays(9))!.Line.UnitPrice.Should().Be(30m);
        PriceRules.Resolve([first, second], null, Gravel, Today.AddDays(10))!.Line.UnitPrice.Should().Be(32m, "the next version starts that day");
        PriceRules.Resolve([first, second], null, Sand, Today.AddDays(10)).Should().BeNull("no line for the product");
    }

    [Fact]
    public void A_product_missing_from_the_customer_version_takes_the_site_price()
    {
        var site = Version(0, null, new PriceLine(Gravel, 30m), new PriceLine(Sand, 18m));
        var own = Version(0, Customer, new PriceLine(Gravel, 27m));

        var sand = PriceRules.Resolve([site, own], Customer, Sand, Today)!;
        sand.Line.UnitPrice.Should().Be(18m);
        sand.Source.Should().Be("site");
    }

    [Fact]
    public void A_withdrawn_version_never_prices_and_a_version_in_force_never_changes()
    {
        var later = Version(5, null, new PriceLine(Gravel, 40m));
        var current = Version(0, null, new PriceLine(Gravel, 30m));

        later.ChangeLines(clock, Today, [new PriceLine(Gravel, 41m)], "tester").Value.Should().BeTrue("not in force yet");
        later.Withdraw(clock, Today, "tester").Value.Should().BeTrue();
        PriceRules.Resolve([current, later], null, Gravel, Today.AddDays(6))!.Line.UnitPrice.Should().Be(30m, "the withdrawn version is skipped");
        later.ChangeLines(clock, Today, [new PriceLine(Gravel, 42m)], "tester").Error.Should().Be(PriceRules.Withdrawn);

        current.ChangeLines(clock, Today, [new PriceLine(Gravel, 31m)], "tester").Error.Should().Be(PriceRules.InForce);
        current.Withdraw(clock, Today, "tester").Error.Should().Be(PriceRules.InForce);
    }

    [Fact]
    public void Prices_are_never_back_dated()
    {
        var result = PriceList.Create(clock, TenantId.From(Guid.NewGuid()), Today, Site, null, "TND", Today.AddDays(-1), [new PriceLine(Gravel, 30m)], "tester");

        result.Error.Should().Be(PriceRules.BackDated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(12.34567)]
    public void A_line_is_a_positive_price_with_at_most_four_decimals(double price)
    {
        PriceRules.Lines([new PriceLine(Gravel, (decimal)price)]).Error.Should().Be(PriceRules.LinesInvalid);
    }

    [Fact]
    public void Lines_name_each_product_once_and_come_back_ordered()
    {
        PriceRules.Lines([new PriceLine(Gravel, 30m), new PriceLine(Gravel, 31m)]).Error.Should().Be(PriceRules.LinesInvalid);
        PriceRules.Lines([]).Error.Should().Be(PriceRules.LinesInvalid);

        var lines = PriceRules.Lines([new PriceLine(Sand, 18m), new PriceLine(Gravel, 30m)]).Value;
        lines.Select(l => l.ProductId).Should().BeInAscendingOrder();
    }

    [Theory]
    [InlineData("tnd", "TND")]
    [InlineData(" eur ", "EUR")]
    public void A_known_currency_is_accepted_in_capitals(string given, string expected)
    {
        PriceRules.Currency(given).Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("XYZ")]
    public void An_unknown_currency_is_refused(string? given)
    {
        PriceRules.Currency(given).Error.Should().Be(PriceRules.CurrencyInvalid);
    }
}
