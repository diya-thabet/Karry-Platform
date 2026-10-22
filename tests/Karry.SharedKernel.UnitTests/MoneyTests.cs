using CsCheck;
using Karry.SharedKernel.Monetary;

namespace Karry.SharedKernel.UnitTests;

[Trait("Story", "US-P1-PLT-01")]
[Trait("TestCase", "TC-P1-PLT-006")]
public sealed class MoneyTests
{
    private static readonly Currency Eur = Currency.Of("EUR");
    private static readonly Currency Tnd = Currency.Of("TND");

    // Amounts up to ±10^12 with 4 decimals: far beyond any quarry invoice, still exact in decimal arithmetic.
    private static readonly Gen<decimal> Amount = Gen.Long[-1_000_000_000_000_0000L, 1_000_000_000_000_0000L].Select(units => units / 10_000m);
    private static readonly Gen<Currency> AnyCurrency = Gen.OneOfConst([.. Currency.SupportedCodes.Select(Currency.Of)]);

    [Fact]
    public void Addition_is_commutative_and_associative_within_a_currency()
    {
        Gen.Select(Amount, Amount, Amount, AnyCurrency).Sample((a, b, c, currency) =>
        {
            Money x = new(a, currency), y = new(b, currency), z = new(c, currency);
            (x + y).Should().Be(y + x);
            ((x + y) + z).Should().Be(x + (y + z));
            (x + y - y).Should().Be(x);
        });
    }

    [Fact]
    public void Combining_different_currencies_is_refused()
    {
        var eur = Money.Of(10m, "EUR");
        var tnd = Money.Of(10m, "TND");

        ((Action)(() => _ = eur + tnd)).Should().Throw<InvalidOperationException>().WithMessage("*EUR*TND*");
        ((Action)(() => _ = eur - tnd)).Should().Throw<InvalidOperationException>();
        ((Action)(() => _ = eur < tnd)).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("EUR", "1.005", "1.01")]
    [InlineData("EUR", "-1.005", "-1.01")]
    [InlineData("EUR", "2.675", "2.68")]
    [InlineData("TND", "1.0005", "1.001")]
    [InlineData("XOF", "1500.5", "1501")]
    [InlineData("XOF", "1499.4", "1499")]
    public void Rounding_uses_minor_units_and_midpoint_away_from_zero(string code, string amount, string expected)
    {
        Money.Of(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), code).Round().Amount
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Rounding_is_idempotent_and_never_moves_more_than_half_a_minor_unit()
    {
        Gen.Select(Amount, AnyCurrency).Sample((a, currency) =>
        {
            var rounded = new Money(a, currency).Round();
            rounded.Round().Should().Be(rounded);
            var halfUnit = 0.5m / (decimal)Math.Pow(10, currency.MinorUnits);
            Math.Abs(rounded.Amount - a).Should().BeLessThanOrEqualTo(halfUnit);
            decimal.Round(rounded.Amount, currency.MinorUnits).Should().Be(rounded.Amount);
        });
    }

    [Fact]
    public void Allocation_never_creates_or_loses_a_minor_unit()
    {
        var ratios = Gen.Int[0, 1000].Select(i => (decimal)i).Array[1, 12].Where(r => r.Sum() > 0m);
        Gen.Select(Amount, AnyCurrency, ratios).Sample((a, currency, r) =>
        {
            var total = new Money(a, currency).Round();
            var parts = total.Allocate(r);

            parts.Should().HaveCount(r.Length);
            parts.Aggregate(Money.Zero(currency), (sum, part) => sum + part).Should().Be(total);
            var minorUnit = 1m / (decimal)Math.Pow(10, currency.MinorUnits);
            for (var i = 0; i < r.Length; i++)
            {
                var exactShare = total.Amount * r[i] / r.Sum();
                Math.Abs(parts[i].Amount - exactShare).Should().BeLessThan(minorUnit, "each part is within one minor unit of its exact share");
                decimal.Round(parts[i].Amount, currency.MinorUnits).Should().Be(parts[i].Amount, "parts are whole minor units");
            }
        });
    }

    [Fact]
    public void Allocating_100_eur_in_three_gives_the_extra_cent_to_the_first_part()
    {
        var parts = Money.Of(100m, "EUR").Allocate([1m, 1m, 1m]);

        parts.Select(p => p.Amount).Should().Equal(33.34m, 33.33m, 33.33m);
    }

    [Fact]
    public void Multiplication_is_not_rounded_until_asked()
    {
        var unitPrice = Money.Of(12.345m, "EUR");

        (unitPrice * 3m).Amount.Should().Be(37.035m);
        (unitPrice * 3m).Round().Amount.Should().Be(37.04m);
    }

    [Theory]
    [InlineData("EUO")]
    [InlineData("eur")]
    [InlineData("")]
    public void Unknown_currency_codes_are_rejected(string code)
    {
        ((Action)(() => Currency.Of(code))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Money_without_a_currency_cannot_exist()
    {
        ((Action)(() => _ = new Money(1m, default))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Invalid_allocation_ratios_are_rejected()
    {
        var money = Money.Of(10m, "EUR");

        ((Action)(() => money.Allocate([]))).Should().Throw<ArgumentException>();
        ((Action)(() => money.Allocate([0m, 0m]))).Should().Throw<ArgumentException>();
        ((Action)(() => money.Allocate([1m, -1m]))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Tunisian_dinar_has_three_decimals()
    {
        Tnd.MinorUnits.Should().Be(3);
        Eur.MinorUnits.Should().Be(2);
        Money.Of(10m, "TND").Allocate([1m, 2m]).Select(p => p.Amount).Should().Equal(3.333m, 6.667m);
    }
}
