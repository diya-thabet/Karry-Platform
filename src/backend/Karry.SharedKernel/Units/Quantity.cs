using System.Globalization;

namespace Karry.SharedKernel.Units;

/// <summary>
/// An amount in a unit (US-P1-UNI-01 AC1, ADR-0010): decimal only, a volume always with its material state. Arithmetic
/// only between quantities of one dimension and, for volumes, one state (10 m³ bank + 5 m³ loose is refused: convert
/// explicitly first, GE-03); the result is in the left operand's unit. Nothing is rounded here.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(QuantityJsonConverter))]
public readonly record struct Quantity : IComparable<Quantity>
{
    public Quantity(decimal value, Unit unit, MaterialState? state = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.HasState && state is null)
        {
            throw new ArgumentException($"A volume in {unit.Code} needs a material state (bank, loose or compacted).", nameof(state));
        }

        if (!unit.HasState && state is not null)
        {
            throw new ArgumentException($"Only a solid volume has a material state, not {unit.Code}.", nameof(state));
        }

        Value = value;
        Unit = unit;
        State = state;
    }

    public decimal Value { get; }

    public Unit Unit { get; }

    public MaterialState? State { get; }

    public static Quantity Tonnes(decimal value) => new(value, Unit.Tonne);

    public static Quantity CubicMetres(decimal value, MaterialState state) => new(value, Unit.CubicMetre, state);

    /// <summary>True when the two can be added, subtracted or compared.</summary>
    public bool IsCompatibleWith(Quantity other) =>
        Unit is not null && other.Unit is not null && Unit.Dimension == other.Unit.Dimension && State == other.State;

    /// <summary>The same amount in another unit of the same dimension (a volume keeps its state).</summary>
    public Quantity To(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Dimension != Unit.Dimension)
        {
            throw new InvalidOperationException($"Cannot express {Unit.Code} in {unit.Code}; convert through a material first.");
        }

        return ReferenceEquals(unit, Unit) ? this : new(Value * Unit.PerBase / unit.PerBase, unit, State);
    }

    public static Quantity operator +(Quantity left, Quantity right) => new(left.Value + Aligned(left, right), left.Unit, left.State);

    public static Quantity operator -(Quantity left, Quantity right) => new(left.Value - Aligned(left, right), left.Unit, left.State);

    public static Quantity operator -(Quantity value) => new(-value.Value, value.Unit, value.State);

    public static Quantity operator *(Quantity value, decimal factor) => new(value.Value * factor, value.Unit, value.State);

    public static Quantity operator *(decimal factor, Quantity value) => value * factor;

    public static bool operator <(Quantity left, Quantity right) => left.CompareTo(right) < 0;

    public static bool operator >(Quantity left, Quantity right) => left.CompareTo(right) > 0;

    public static bool operator <=(Quantity left, Quantity right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Quantity left, Quantity right) => left.CompareTo(right) >= 0;

    public static Quantity Add(Quantity left, Quantity right) => left + right;

    public static Quantity Subtract(Quantity left, Quantity right) => left - right;

    public static Quantity Negate(Quantity value) => -value;

    public static Quantity Multiply(Quantity value, decimal factor) => value * factor;

    public int CompareTo(Quantity other) => Value.CompareTo(Aligned(this, other));

    public override string ToString() =>
        State is { } state
            ? string.Create(CultureInfo.InvariantCulture, $"{Value} {Unit.Code} {MaterialStates.Code(state)}")
            : string.Create(CultureInfo.InvariantCulture, $"{Value} {Unit?.Code}");

    /// <summary>The right operand's value in the left one's unit, or a refusal.</summary>
    private static decimal Aligned(Quantity left, Quantity right) =>
        left.IsCompatibleWith(right)
            ? right.To(left.Unit).Value
            : throw new InvalidOperationException($"Cannot combine {Describe(left)} with {Describe(right)}; convert explicitly first.");

    private static string Describe(Quantity q) => q.State is { } state ? $"{q.Unit?.Code} {MaterialStates.Code(state)}" : q.Unit?.Code ?? "nothing";
}
