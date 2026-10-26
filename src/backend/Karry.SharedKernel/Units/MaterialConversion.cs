using Karry.SharedKernel.Results;

namespace Karry.SharedKernel.Units;

/// <summary>
/// The physical properties a conversion uses (engine §10.1): dry density in t/m³ for each state the material has (a
/// product never in the bench has no bank density), and the moisture content as a mass fraction. The swell factor is
/// not stored: it is <c>ρ_bank / ρ_loose − 1</c>, so a state change through mass always agrees with it.
/// </summary>
public sealed record MaterialDensities(decimal? Bank, decimal? Loose, decimal? Compacted, decimal Moisture)
{
    public decimal? Of(MaterialState state) => state switch
    {
        MaterialState.Bank => Bank,
        MaterialState.Loose => Loose,
        MaterialState.Compacted => Compacted,
        _ => null,
    };

    /// <summary><c>s = ρ_bank / ρ_loose − 1</c>, when the material has both.</summary>
    public decimal? Swell => Bank is { } bank && Loose is { } loose ? (bank / loose) - 1m : null;
}

/// <summary>A factor a conversion used, to show the person how the number was obtained (US-P1-UNI-01 AC4).</summary>
public sealed record ConversionFactor(string Name, decimal Value, string Unit);

/// <summary>A converted quantity and the factors behind it.</summary>
public sealed record Conversion(Quantity Result, IReadOnlyList<ConversionFactor> Factors);

/// <summary>
/// Volume ↔ mass ↔ volume in another state with a material's densities (US-P1-UNI-01 AC2–AC3, ADR-0010):
/// <c>M = V_state × ρ_state × (1 + w)</c> and <c>V_state = M / (ρ_state × (1 + w))</c>. A state change goes through
/// mass, which equals <c>V_loose = V_bank × (1 + s)</c>. Units of one dimension convert without a material. Decimal
/// arithmetic, nothing rounded.
/// </summary>
public static class MaterialConversion
{
    public static readonly Error NotConvertible =
        Error.Validation("units.not_convertible", "These units cannot be converted into each other.");

    public static Error DensityMissing(MaterialState state) =>
        Error.Validation("units.density_missing", "The material has no density for this state.").With("state", MaterialStates.Code(state));

    /// <summary>
    /// <paramref name="quantity"/> in <paramref name="unit"/> (and <paramref name="state"/> for m³). A different
    /// moisture than the material's default can be given (a measured sample).
    /// </summary>
    public static Result<Conversion> Convert(Quantity quantity, Unit unit, MaterialState? state, MaterialDensities densities, decimal? moisture = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(densities);
        if (unit.HasState != state.HasValue)
        {
            return NotConvertible;
        }

        var from = quantity.Unit.Dimension;
        if (from == unit.Dimension && quantity.State == state)
        {
            return new Conversion(quantity.To(unit), []);
        }

        if (from is not (Dimension.Volume or Dimension.Mass) || unit.Dimension is not (Dimension.Volume or Dimension.Mass))
        {
            return NotConvertible;
        }

        var w = moisture ?? densities.Moisture;
        List<ConversionFactor> factors = [];
        decimal tonnes;
        if (from == Dimension.Mass)
        {
            tonnes = quantity.To(Unit.Tonne).Value;
        }
        else
        {
            var source = quantity.State!.Value;
            if (densities.Of(source) is not { } density)
            {
                return DensityMissing(source);
            }

            factors.Add(Density(source, density));
            tonnes = quantity.Value * density * (1m + w);
        }

        if (unit.Dimension == Dimension.Mass)
        {
            factors.Add(new ConversionFactor("moisture", w, "fraction"));
            return new Conversion(Quantity.Tonnes(tonnes).To(unit), factors);
        }

        var target = state!.Value;
        if (densities.Of(target) is not { } targetDensity)
        {
            return DensityMissing(target);
        }

        factors.Add(Density(target, targetDensity));
        factors.Add(new ConversionFactor("moisture", w, "fraction"));
        if (from == Dimension.Volume && densities.Swell is { } swell && target != quantity.State && target is MaterialState.Bank or MaterialState.Loose
            && quantity.State is MaterialState.Bank or MaterialState.Loose)
        {
            factors.Add(new ConversionFactor("swell", swell, "fraction"));
        }

        return new Conversion(new Quantity(tonnes / (targetDensity * (1m + w)), unit, target), factors);
    }

    private static ConversionFactor Density(MaterialState state, decimal value) => new($"density.{MaterialStates.Code(state)}", value, "t/m3");
}
