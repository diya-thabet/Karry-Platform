using System.Collections.Frozen;

namespace Karry.SharedKernel.Monetary;

/// <summary>
/// ISO 4217 currency with its number of minor units. Only currencies Karry supports are accepted, so a typo
/// ("EUO") fails loudly instead of creating a new currency.
/// </summary>
public readonly record struct Currency
{
    // Minor units per ISO 4217. Extend deliberately when a new market is opened.
    private static readonly FrozenDictionary<string, int> MinorUnitsByCode = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["EUR"] = 2,
        ["USD"] = 2,
        ["GBP"] = 2,
        ["CHF"] = 2,
        ["CAD"] = 2,
        ["TND"] = 3,
        ["MAD"] = 2,
        ["DZD"] = 2,
        ["EGP"] = 2,
        ["LYD"] = 3,
        ["XOF"] = 0,
        ["XAF"] = 0,
        ["SAR"] = 2,
        ["AED"] = 2,
        ["QAR"] = 2,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    public string Code { get; }

    /// <summary>Digits after the decimal point (EUR 2, TND 3, XOF 0).</summary>
    public int MinorUnits { get; }

    public static IReadOnlyCollection<string> SupportedCodes => MinorUnitsByCode.Keys;

    public static Currency Of(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return MinorUnitsByCode.TryGetValue(code, out var minorUnits)
            ? new Currency(code, minorUnits)
            : throw new ArgumentException($"Unsupported currency '{code}'. Supported: {string.Join(", ", SupportedCodes)}.", nameof(code));
    }

    public override string ToString() => Code;
}
