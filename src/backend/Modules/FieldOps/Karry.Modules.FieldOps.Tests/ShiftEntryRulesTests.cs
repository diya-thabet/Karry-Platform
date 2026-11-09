using System.Globalization;
using CsCheck;
using Karry.Modules.FieldOps.Domain;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>
/// The rules of a shift entry's values (ADR-0034 §3, §6) as properties over random meters, times and values
/// (10 000 cases each): meter values on the wire, Δ (RULE-FO-02), continuity (RULE-FO-01) and overlaps.
/// </summary>
[Trait("Story", "US-P2-FO-04")]
public sealed class ShiftEntryRulesTests
{
    private const int Cases = 10_000;

    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Meter values as stored: tenths from 0 to 9 999 999.9.
    private static readonly Gen<decimal> Value = Gen.Long[0, 99_999_999].Select(tenths => tenths / 10m);

    private static readonly Gen<DateTimeOffset> Time = Gen.Int[0, 60 * 24 * 365].Select(minutes => Origin.AddMinutes(minutes));

    [Fact]
    [Trait("TestCase", "TC-P2-FO-005")]
    public void An_hour_meter_is_flagged_exactly_when_it_goes_back_or_runs_more_than_the_segment_and_half_an_hour()
    {
        Gen.Select(Time, Gen.Int[1, 24 * 60], Value, Gen.Int[-50, 300]).Sample((start, minutes, startValue, deltaTenths) =>
        {
            var end = start.AddMinutes(minutes);
            var endValue = Math.Max(0, startValue + (deltaTenths / 10m));
            var delta = endValue - startValue;
            var expected = delta < 0 || delta * 60 > minutes + 30;
            return ShiftEntryRules.EndFlags("hours", start, startValue, end, endValue).Contains(ShiftEntryFlags.Delta) == expected;
        }, iter: Cases);
    }

    [Fact]
    public void A_kilometre_meter_is_flagged_only_when_it_goes_back()
    {
        Gen.Select(Time, Gen.Int[1, 24 * 60], Value, Value).Sample((start, minutes, startValue, endValue) =>
            ShiftEntryRules.EndFlags("km", start, startValue, start.AddMinutes(minutes), endValue).Contains(ShiftEntryFlags.Delta) == endValue < startValue, iter: Cases);
    }

    [Fact]
    public void An_end_at_or_before_its_start_is_always_flagged_and_never_otherwise()
    {
        Gen.Select(Time, Gen.Int[-24 * 60, 24 * 60], Value, Value).Sample((start, minutes, startValue, endValue) =>
            ShiftEntryRules.EndFlags("hours", start, startValue, start.AddMinutes(minutes), endValue).Contains(ShiftEntryFlags.EndBeforeStart) == minutes <= 0, iter: Cases);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-003")]
    public void A_start_is_flagged_for_continuity_exactly_when_it_differs_from_the_end_it_continues()
    {
        Gen.Select(Value, Value, Gen.Bool).Sample((end, start, known) =>
        {
            var flags = ShiftEntryRules.StartFlags(known ? end : null, start);
            return flags.Contains(ShiftEntryFlags.Continuity) == (known && end != start);
        }, iter: Cases);
        ShiftEntryRules.StartFlags(12482.5m, 12482.50m).Should().BeEmpty("the same value with another scale is the same value");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-011")]
    public void Two_segments_overlap_exactly_when_some_minute_lies_in_both()
    {
        var segment = Gen.Select(Gen.Int[0, 200], Gen.Int[-5, 60], Gen.Bool)
            .Select((start, length, running) => (Start: start, End: running ? (int?)null : start + length));
        Gen.Select(segment, segment).Sample((a, b) =>
        {
            static bool Holds((int Start, int? End) s, int minute) => minute >= s.Start && minute < (s.End ?? int.MaxValue);
            var shared = Enumerable.Range(0, 300).Any(minute => Holds(a, minute) && Holds(b, minute));
            var overlaps = ShiftEntryRules.Overlaps(At(a.Start), At(a.End), At(b.Start), At(b.End));
            return overlaps == shared && overlaps == ShiftEntryRules.Overlaps(At(b.Start), At(b.End), At(a.Start), At(a.End));
        }, iter: Cases);
    }

    [Fact]
    public void Every_stored_meter_value_reads_back_exactly_from_its_wire_text()
    {
        Value.Sample(value =>
        {
            var text = value.ToString(value == decimal.Truncate(value) ? "0" : "0.0", CultureInfo.InvariantCulture);
            return ShiftEntryRules.TryParseMeterValue(text, out var read) && read == value;
        }, iter: Cases);
    }

    [Theory]
    [InlineData("12482.55")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1e3")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("10000000")]
    [InlineData(" 1")]
    [InlineData("١٢")]
    public void Meter_values_with_another_shape_are_not_read(string text) =>
        ShiftEntryRules.TryParseMeterValue(text, out _).Should().BeFalse();

    private static DateTimeOffset? At(int? minute) => minute is { } m ? Origin.AddMinutes(m) : null;

    private static DateTimeOffset At(int minute) => Origin.AddMinutes(minute);
}
