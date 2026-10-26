using System.Text.Json;
using CsCheck;
using Karry.SharedKernel.Units;

namespace Karry.SharedKernel.UnitTests;

/// <summary>
/// The units engine (US-P1-UNI-01, ADR-0010, engine §10.1). Expected values of the golden examples are the formula
/// handbook's hand-computed literals (formulas-worked-examples §1), never re-derived from the code.
/// </summary>
[Trait("Story", "US-P1-UNI-01")]
public sealed class QuantityTests
{
    private static readonly Gen<MaterialState> AnyState = Gen.Enum<MaterialState>();

    // Handbook valid ranges: densities 1.2–3.2 t/m³ (3 decimals), moisture 0–0.25 (3 decimals).
    private static readonly Gen<decimal> Density = Gen.Int[1200, 3200].Select(d => d / 1000m);
    private static readonly Gen<decimal> Moisture = Gen.Int[0, 250].Select(w => w / 1000m);

    // 1 kg to 100,000 t with 3 decimals: a sample bag to a month of production.
    private static readonly Gen<decimal> Tonnes = Gen.Long[1, 100_000_000L].Select(kg => kg / 1000m);

    private static MaterialDensities Uniform(decimal density, decimal moisture) => new(density, density, density, moisture);

    private static decimal Rounded(decimal value, int decimals) => decimal.Round(value, decimals, MidpointRounding.AwayFromZero);

    [Fact]
    [Trait("GoldenExample", "GE-01")]
    public void GE01_invoice_volume_from_weighbridge_mass()
    {
        var gravel = new MaterialDensities(Bank: null, Loose: 1.50m, Compacted: null, Moisture: 0.02m);

        var conversion = MaterialConversion.Convert(Quantity.Tonnes(28.40m), Unit.CubicMetre, MaterialState.Loose, gravel).Value;

        conversion.Result.Unit.Should().Be(Unit.CubicMetre);
        conversion.Result.State.Should().Be(MaterialState.Loose);
        Rounded(conversion.Result.Value, 3).Should().Be(18.562m);
        conversion.Factors.Should().Equal(new ConversionFactor("density.loose", 1.50m, "t/m3"), new ConversionFactor("moisture", 0.02m, "fraction"));
    }

    [Fact]
    [Trait("GoldenExample", "GE-02")]
    public void GE02_loose_to_bank_via_swell_factor()
    {
        // Blasted limestone, s = 0.60: any bank and loose densities with ρ_bank / ρ_loose = 1.60.
        var limestone = new MaterialDensities(Bank: 2.56m, Loose: 1.60m, Compacted: null, Moisture: 0.03m);
        limestone.Swell.Should().Be(0.60m);

        var conversion = MaterialConversion.Convert(Quantity.CubicMetres(18.562m, MaterialState.Loose), Unit.CubicMetre, MaterialState.Bank, limestone).Value;

        conversion.Result.State.Should().Be(MaterialState.Bank);
        Rounded(conversion.Result.Value, 3).Should().Be(11.601m);
        conversion.Factors.Should().Contain(new ConversionFactor("swell", 0.60m, "fraction"));
    }

    [Fact]
    [Trait("GoldenExample", "GE-03")]
    [Trait("TestCase", "TC-P1-UNI-003")]
    public void GE03_bank_and_loose_volumes_do_not_add()
    {
        var bank = Quantity.CubicMetres(10m, MaterialState.Bank);
        var loose = Quantity.CubicMetres(5m, MaterialState.Loose);

        ((Action)(() => _ = bank + loose)).Should().Throw<InvalidOperationException>().WithMessage("*m3 bank*m3 loose*convert explicitly*");
        ((Action)(() => _ = bank - loose)).Should().Throw<InvalidOperationException>();
        ((Action)(() => _ = bank < loose)).Should().Throw<InvalidOperationException>();
        bank.IsCompatibleWith(loose).Should().BeFalse();
        (bank + Quantity.CubicMetres(5m, MaterialState.Bank)).Should().Be(Quantity.CubicMetres(15m, MaterialState.Bank));
    }

    [Fact]
    [Trait("TestCase", "TC-P1-UNI-003")]
    public void Different_dimensions_never_combine()
    {
        var pairs = new (Quantity, Quantity)[]
        {
            (Quantity.Tonnes(1m), Quantity.CubicMetres(1m, MaterialState.Loose)),
            (new Quantity(8m, Unit.Hour), new Quantity(1m, Unit.Day)),
            (new Quantity(1000m, Unit.Litre), Quantity.CubicMetres(1m, MaterialState.Loose)),
            (new Quantity(1m, Unit.Kilometre), new Quantity(1m, Unit.Piece)),
        };

        foreach (var (left, right) in pairs)
        {
            ((Action)(() => _ = left + right)).Should().Throw<InvalidOperationException>($"{left} + {right}");
            ((Action)(() => _ = left.To(right.Unit))).Should().Throw<InvalidOperationException>();
        }
    }

    [Fact]
    public void Mass_units_combine_exactly_in_the_left_unit()
    {
        (Quantity.Tonnes(1m) + new Quantity(250m, Unit.Kilogram)).Should().Be(Quantity.Tonnes(1.25m));
        (new Quantity(250m, Unit.Kilogram) + Quantity.Tonnes(1m)).Should().Be(new Quantity(1250m, Unit.Kilogram));
        new Quantity(1m, Unit.ShortTon).To(Unit.Tonne).Should().Be(Quantity.Tonnes(0.90718474m), "1 short ton = 0.90718474 t exactly");
        new Quantity(2000m, Unit.Kilogram).To(Unit.Tonne).Value.Should().Be(2m);
        (Quantity.Tonnes(1m) > new Quantity(999m, Unit.Kilogram)).Should().BeTrue();
        (Quantity.Tonnes(2m) * 1.5m).Should().Be(Quantity.Tonnes(3m));
    }

    [Fact]
    public void Subtracts_compares_and_negates_within_a_dimension()
    {
        var ten = Quantity.CubicMetres(10m, MaterialState.Compacted);
        var four = Quantity.CubicMetres(4m, MaterialState.Compacted);
        var same = Quantity.CubicMetres(4.0m, MaterialState.Compacted);

        (ten - four).Should().Be(Quantity.CubicMetres(6m, MaterialState.Compacted));
        Quantity.Subtract(ten, four).Should().Be(Quantity.CubicMetres(6m, MaterialState.Compacted));
        Quantity.Add(ten, four).Should().Be(Quantity.CubicMetres(14m, MaterialState.Compacted));
        (-four).Should().Be(Quantity.CubicMetres(-4m, MaterialState.Compacted));
        Quantity.Negate(four).Value.Should().Be(-4m);
        (four < ten).Should().BeTrue();
        (ten < four).Should().BeFalse();
        (four < same).Should().BeFalse();
        (ten > four).Should().BeTrue();
        (four > same).Should().BeFalse();
        (four <= same).Should().BeTrue();
        (ten <= four).Should().BeFalse();
        (four >= same).Should().BeTrue();
        (four >= ten).Should().BeFalse();
        (Quantity.Tonnes(1m) - new Quantity(1000m, Unit.Kilogram)).Value.Should().Be(0m);
    }

    [Fact]
    public void An_empty_quantity_is_compatible_with_nothing()
    {
        default(Quantity).IsCompatibleWith(Quantity.Tonnes(1m)).Should().BeFalse();
        Quantity.Tonnes(1m).IsCompatibleWith(default).Should().BeFalse();
        ((Action)(() => _ = Quantity.Tonnes(1m) + default(Quantity))).Should().Throw<InvalidOperationException>().WithMessage("*nothing*");
    }

    [Fact]
    public void Units_are_found_by_their_code()
    {
        Unit.Of("st").Should().BeSameAs(Unit.ShortTon);
        Unit.All.Select(u => u.Code).Should().Equal("m3", "l", "t", "kg", "st", "km", "h", "day", "pcs");
        ((Action)(() => Unit.Of("yd3"))).Should().Throw<ArgumentException>().WithMessage("*yd3*");
        Unit.Find(null).Should().BeNull();
    }

    [Fact]
    public void A_volume_has_a_state_and_nothing_else_does()
    {
        ((Action)(() => _ = new Quantity(1m, Unit.CubicMetre))).Should().Throw<ArgumentException>().WithMessage("*state*");
        ((Action)(() => _ = new Quantity(1m, Unit.Tonne, MaterialState.Bank))).Should().Throw<ArgumentException>();
        ((Action)(() => _ = new Quantity(1m, Unit.Litre, MaterialState.Loose))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Conversions_without_a_meaning_or_a_density_are_refused()
    {
        var gravel = new MaterialDensities(null, 1.50m, null, 0.02m);

        MaterialConversion.Convert(new Quantity(100m, Unit.Litre), Unit.Tonne, null, gravel).Error.Should().Be(MaterialConversion.NotConvertible);
        MaterialConversion.Convert(new Quantity(8m, Unit.Hour), Unit.Day, null, gravel).Error.Should().Be(MaterialConversion.NotConvertible);
        MaterialConversion.Convert(Quantity.Tonnes(1m), Unit.CubicMetre, null, gravel).Error.Should().Be(MaterialConversion.NotConvertible, "a volume needs its state");

        var missing = MaterialConversion.Convert(Quantity.Tonnes(1m), Unit.CubicMetre, MaterialState.Bank, gravel);
        missing.Error!.Code.Should().Be("units.density_missing");
        missing.Error!.Extensions["state"].Should().Be("bank");
        MaterialConversion.Convert(Quantity.CubicMetres(1m, MaterialState.Compacted), Unit.Tonne, null, gravel).Error!.Code.Should().Be("units.density_missing");
    }

    [Fact]
    public void A_measured_moisture_replaces_the_default()
    {
        var gravel = new MaterialDensities(null, 1.50m, null, 0.02m);

        var wet = MaterialConversion.Convert(Quantity.CubicMetres(10m, MaterialState.Loose), Unit.Tonne, null, gravel, moisture: 0.10m).Value;

        wet.Result.Should().Be(Quantity.Tonnes(16.5m), "10 × 1.50 × 1.10");
        wet.Factors.Should().Contain(new ConversionFactor("moisture", 0.10m, "fraction"));
    }

    [Fact]
    [Trait("TestCase", "TC-P1-UNI-001")]
    public void Tonnes_to_cubic_metres_and_back_is_exact_to_1e9_relative()
    {
        Gen.Select(Tonnes, Density, Moisture, AnyState).Sample((tonnes, density, moisture, state) =>
        {
            var material = Uniform(density, moisture);
            var volume = MaterialConversion.Convert(Quantity.Tonnes(tonnes), Unit.CubicMetre, state, material).Value.Result;
            var back = MaterialConversion.Convert(volume, Unit.Tonne, null, material).Value.Result;

            Math.Abs(back.Value - tonnes).Should().BeLessThanOrEqualTo(tonnes * 1e-9m);
        }, iter: 10_000);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-UNI-001")]
    public void Conversions_are_monotonic()
    {
        Gen.Select(Tonnes, Tonnes, Density, Moisture, AnyState).Sample((a, b, density, moisture, state) =>
        {
            var material = Uniform(density, moisture);
            var volumeA = MaterialConversion.Convert(Quantity.Tonnes(a), Unit.CubicMetre, state, material).Value.Result.Value;
            var volumeB = MaterialConversion.Convert(Quantity.Tonnes(b), Unit.CubicMetre, state, material).Value.Result.Value;

            Math.Sign(volumeA - volumeB).Should().Be(Math.Sign(a - b));
        });
    }

    [Fact]
    public void A_state_change_through_mass_agrees_with_the_swell_factor()
    {
        Gen.Select(Tonnes, Density, Density, Moisture).Sample((bankVolume, bank, loose, moisture) =>
        {
            var material = new MaterialDensities(bank, loose, null, moisture);

            var result = MaterialConversion.Convert(Quantity.CubicMetres(bankVolume, MaterialState.Bank), Unit.CubicMetre, MaterialState.Loose, material).Value.Result;

            // V_loose = V_bank × (1 + s), with s = ρ_bank / ρ_loose − 1.
            var expected = bankVolume * (1m + ((bank / loose) - 1m));
            Math.Abs(result.Value - expected).Should().BeLessThanOrEqualTo(expected * 1e-9m);
        });
    }

    [Fact]
    public void Serialises_as_value_unit_and_state()
    {
        JsonSerializer.Serialize(Quantity.CubicMetres(18.562m, MaterialState.Loose)).Should().Be("""{"value":18.562,"unit":"m3","state":"loose"}""");
        JsonSerializer.Serialize(new Quantity(28.40m, Unit.Tonne)).Should().Be("""{"value":28.40,"unit":"t","state":null}""");

        JsonSerializer.Deserialize<Quantity>("""{"value":0.1,"unit":"m3","state":"bank"}""").Should().Be(Quantity.CubicMetres(0.1m, MaterialState.Bank), "decimal, not 0.1 as a double");
        JsonSerializer.Deserialize<Quantity>("""{"unit":"st","value":3}""").Should().Be(new Quantity(3m, Unit.ShortTon));
        foreach (var state in Enum.GetValues<MaterialState>())
        {
            var quantity = Quantity.CubicMetres(2.5m, state);
            JsonSerializer.Deserialize<Quantity>(JsonSerializer.Serialize(quantity)).Should().Be(quantity);
        }


        foreach (var invalid in new[] { """{"value":1,"unit":"m3"}""", """{"value":1,"unit":"t","state":"bank"}""", """{"value":1,"unit":"yd3"}""", """{"value":"1","unit":"t"}""", "[1]" })
        {
            ((Action)(() => JsonSerializer.Deserialize<Quantity>(invalid))).Should().Throw<JsonException>(invalid);
        }
    }
}
