using System.Globalization;

namespace Karry.SharedKernel.Monetary;

/// <summary>
/// An amount in one currency. Decimal only (never float), arithmetic only between equal currencies,
/// rounding explicit: <see cref="Round"/> uses the currency's minor units and commercial rounding
/// (midpoint away from zero), as on invoices.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public Money(decimal amount, Currency currency)
    {
        if (currency.Code is null)
        {
            throw new ArgumentException("Money needs a currency (default(Currency) is not one).", nameof(currency));
        }

        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public static Money Zero(Currency currency) => new(0m, currency);

    public static Money Of(decimal amount, string currencyCode) => new(amount, Currency.Of(currencyCode));

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public static Money operator +(Money left, Money right) => new(left.Amount + SameCurrency(left, right).Amount, left.Currency);

    public static Money operator -(Money left, Money right) => new(left.Amount - SameCurrency(left, right).Amount, left.Currency);

    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    /// <summary>Unrounded product (e.g. unit price × quantity); call <see cref="Round"/> where the business rounds.</summary>
    public static Money operator *(Money value, decimal factor) => new(value.Amount * factor, value.Currency);

    public static Money operator *(decimal factor, Money value) => value * factor;

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public static Money Add(Money left, Money right) => left + right;

    public static Money Subtract(Money left, Money right) => left - right;

    public static Money Negate(Money value) => -value;

    public static Money Multiply(Money value, decimal factor) => value * factor;

    public int CompareTo(Money other) => Amount.CompareTo(SameCurrency(this, other).Amount);

    /// <summary>Rounds to the currency's minor units, midpoint away from zero (1.005 EUR → 1.01 EUR).</summary>
    public Money Round() => new(decimal.Round(Amount, Currency.MinorUnits, MidpointRounding.AwayFromZero), Currency);

    /// <summary>
    /// Splits the (rounded) amount by ratios without creating or losing a minor unit: the parts always sum to the
    /// rounded total. Remainders go to the parts with the largest fractional share (largest remainder method).
    /// </summary>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<decimal> ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);
        if (ratios.Count == 0 || ratios.Any(r => r < 0m) || ratios.Sum() <= 0m)
        {
            throw new ArgumentException("Ratios must be non-negative and sum to more than zero.", nameof(ratios));
        }

        var unit = Pow10(Currency.MinorUnits);
        var totalUnits = decimal.Round(Amount, Currency.MinorUnits, MidpointRounding.AwayFromZero) * unit;
        var ratioSum = ratios.Sum();

        var exact = ratios.Select(r => totalUnits * r / ratioSum).ToArray();
        var floored = exact.Select(e => decimal.Floor(e)).ToArray();
        var remainder = (int)(totalUnits - floored.Sum());

        // Give the leftover minor units to the largest fractional parts (ties: lowest index first, deterministic).
        foreach (var index in exact.Select((e, i) => (Fraction: e - floored[i], Index: i))
                     .OrderByDescending(x => x.Fraction).ThenBy(x => x.Index)
                     .Take(Math.Abs(remainder))
                     .Select(x => x.Index))
        {
            floored[index] += Math.Sign(remainder);
        }

        var currency = Currency;
        return [.. floored.Select(units => new Money(units / unit, currency))];
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Amount} {Currency.Code}");

    private static Money SameCurrency(Money left, Money right) =>
        left.Currency == right.Currency
            ? right
            : throw new InvalidOperationException($"Cannot combine {left.Currency.Code} with {right.Currency.Code}; convert explicitly first.");

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
