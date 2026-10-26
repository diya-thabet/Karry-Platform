namespace Karry.SharedKernel.Units;

/// <summary>What a unit measures; only units of the same dimension combine or convert without a material.</summary>
public enum Dimension
{
    /// <summary>Solid volume (m³), always with a <see cref="MaterialState"/>.</summary>
    Volume,

    /// <summary>Liquid volume (litres: fuel, oil); never converted to a solid volume.</summary>
    LiquidVolume,
    Mass,
    Length,

    /// <summary>Operating hours (engine hours, labour).</summary>
    Hours,

    /// <summary>Calendar days (rental, standing time): not 24 operating hours, so never converted to hours.</summary>
    Days,
    Count,
}

/// <summary>Where a volume of rock is (ADR-0010): in the bench, broken in a truck or stockpile, or compacted in a fill.</summary>
public enum MaterialState
{
    Bank,
    Loose,
    Compacted,
}

/// <summary>
/// A unit of measure (US-P1-UNI-01 AC1, ADR-0010). Units of one dimension convert exactly through
/// <see cref="PerBase"/> (how many base units one of this unit is: 1 kg = 0.001 t, 1 short ton = 0.90718474 t).
/// </summary>
public sealed class Unit
{
    public static readonly Unit CubicMetre = new("m3", Dimension.Volume, 1m);
    public static readonly Unit Litre = new("l", Dimension.LiquidVolume, 1m);
    public static readonly Unit Tonne = new("t", Dimension.Mass, 1m);
    public static readonly Unit Kilogram = new("kg", Dimension.Mass, 0.001m);

    /// <summary>US short ton: exactly 2,000 lb = 0.90718474 t.</summary>
    public static readonly Unit ShortTon = new("st", Dimension.Mass, 0.90718474m);
    public static readonly Unit Kilometre = new("km", Dimension.Length, 1m);
    public static readonly Unit Hour = new("h", Dimension.Hours, 1m);
    public static readonly Unit Day = new("day", Dimension.Days, 1m);
    public static readonly Unit Piece = new("pcs", Dimension.Count, 1m);

    public static IReadOnlyList<Unit> All { get; } = [CubicMetre, Litre, Tonne, Kilogram, ShortTon, Kilometre, Hour, Day, Piece];

    private Unit(string code, Dimension dimension, decimal perBase)
    {
        Code = code;
        Dimension = dimension;
        PerBase = perBase;
    }

    /// <summary>Stable lower-case code, as the API and the database write it.</summary>
    public string Code { get; }

    public Dimension Dimension { get; }

    public decimal PerBase { get; }

    /// <summary>Only m³ carries a material state, and it always does.</summary>
    public bool HasState => Dimension == Dimension.Volume;

    public static Unit? Find(string? code) => All.FirstOrDefault(u => string.Equals(u.Code, code, StringComparison.Ordinal));

    public static Unit Of(string code) => Find(code) ?? throw new ArgumentException($"Unknown unit '{code}'.", nameof(code));

    public override string ToString() => Code;
}

public static class MaterialStates
{
    public static string Code(MaterialState state) => state switch
    {
        MaterialState.Bank => "bank",
        MaterialState.Loose => "loose",
        MaterialState.Compacted => "compacted",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static MaterialState? Find(string? code) => code switch
    {
        "bank" => MaterialState.Bank,
        "loose" => MaterialState.Loose,
        "compacted" => MaterialState.Compacted,
        _ => null,
    };
}
