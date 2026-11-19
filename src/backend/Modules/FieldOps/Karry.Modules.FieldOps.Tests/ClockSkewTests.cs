using CsCheck;
using Karry.Modules.FieldOps.Domain;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>
/// The clock skew rule (RULE-FO-03, threat T-04) as a pure function: a known offset beyond the tolerance, or a device
/// time later than the server received the fact; an offline delay of any length is never a skew.
/// </summary>
[Trait("Story", "US-P2-INT-01")]
public sealed class ClockSkewTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(10);
    private const long Hour = 60 * 60 * 1000;

    [Fact]
    [Trait("TestCase", "TC-P2-SYNC-008")]
    public void A_tablet_that_knew_its_clock_was_three_hours_ahead_is_flagged_by_its_known_offset()
    {
        // The clock reads 3 h ahead, the tablet measured it (server − tablet = −3 h) and signed at once.
        ClockSkew.Judge(Received.AddHours(3), -3 * Hour, Received, Tolerance).Should().Be(new SkewFound(ClockSkew.KnownOffset, 180));
        ClockSkew.Judge(Received.AddHours(-2), 2 * Hour, Received.AddHours(5), Tolerance).Should().Be(new SkewFound(ClockSkew.KnownOffset, -120));
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SYNC-008")]
    public void A_clock_moved_forward_after_its_last_measure_is_flagged_as_ahead_of_the_server()
    {
        ClockSkew.Judge(Received.AddHours(3), 0, Received.AddMinutes(30), Tolerance).Should().Be(new SkewFound(ClockSkew.Ahead, 150));
        ClockSkew.Judge(Received.AddMinutes(10), 0, Received, Tolerance).Should().BeNull("the tolerance itself is not a skew");
        ClockSkew.Judge(Received.AddMinutes(10).AddMilliseconds(1), 0, Received, Tolerance).Should().Be(new SkewFound(ClockSkew.Ahead, 10));
    }

    [Fact]
    public void An_offset_at_the_tolerance_is_not_a_skew_and_one_millisecond_more_is() =>
        (ClockSkew.Judge(Received, 10 * 60 * 1000, Received, Tolerance), ClockSkew.Judge(Received.AddMinutes(-20), 10 * 60 * 1000 + 1, Received, Tolerance))
            .Should().Be((null, new SkewFound(ClockSkew.KnownOffset, -10)));

    [Fact]
    public void An_offline_delay_of_any_length_with_a_small_offset_is_never_a_skew() =>
        Gen.Select(Gen.Long[-10 * 60 * 1000, 10 * 60 * 1000], Gen.Long[0, 30L * 24 * Hour]).Sample((offset, delay) =>
            // Signed `delay` ms before it arrived, on a clock `offset` ms behind the server.
            ClockSkew.Judge(Received.AddMilliseconds(-delay - offset), offset, Received, Tolerance) is null);

    [Fact]
    public void A_known_offset_beyond_the_tolerance_is_always_flagged_with_its_minutes() =>
        Gen.Select(Gen.Long[10 * 60 * 1000 + 1, ClockSkew.MaxOffsetMs], Gen.Bool, Gen.Long[0, 30L * 24 * Hour]).Sample((size, behind, delay) =>
        {
            var offset = behind ? size : -size;
            var found = ClockSkew.Judge(Received.AddMilliseconds(-delay - offset), offset, Received, Tolerance);
            return found is { Reason: ClockSkew.KnownOffset } && found.MinutesAhead == (long)Math.Round(-offset / 60_000.0, MidpointRounding.AwayFromZero);
        });
}
