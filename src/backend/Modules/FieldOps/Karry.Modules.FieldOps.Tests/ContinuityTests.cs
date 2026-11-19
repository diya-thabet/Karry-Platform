using CsCheck;
using Karry.Modules.FieldOps.Domain;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>
/// The continuity check (US-P2-FO-07, ADR-0036 §4) as a pure function: a gap exactly where a start does not continue the
/// end just before it in shift-time order, whatever order the segments arrive in.
/// </summary>
[Trait("Story", "US-P2-FO-07")]
public sealed class ContinuityTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
    private static readonly string Photo = "sha256:" + new string('a', 64);

    /// <summary>
    /// A meter's history built to plan: each segment continues the previous end, or is moved off it by a planned gap, or
    /// follows a segment that never ended (nothing to compare with); the segments come in shuffled.
    /// </summary>
    private sealed record History(IReadOnlyList<MeterSegment> Arrival, IReadOnlySet<string> PlannedGaps);

    private sealed record Plan(int Minutes, int Tenths, int Gap, bool Open, int Arrival);

    private static readonly Gen<History> Histories =
        Gen.Select(Gen.Int[0, 100_000], Gen.Select(Gen.Int[1, 900], Gen.Int[0, 200], Gen.Int[-500, 500].Select(Gen.Int[0, 3]), Gen.Int[0, 5], Gen.Int)
            .Select((minutes, tenths, gap, open, arrival) => new Plan(minutes, tenths, gap.Item2 == 0 ? gap.Item1 : 0, open == 0, arrival)).Array[1, 25])
        .Select((first, plans) =>
        {
            var segments = new List<(MeterSegment Segment, int Arrival)>();
            var gaps = new HashSet<string>(StringComparer.Ordinal);
            var startedAt = Day;
            MeterSegment? before = null;
            foreach (var (plan, index) in plans.Select((p, i) => (p, i)))
            {
                startedAt = startedAt.AddMinutes(plan.Minutes);
                var startValue = before?.EndValue is { } end ? end + (plan.Gap / 10m) : first / 10m + (plan.Gap / 10m);
                var segment = new MeterSegment(
                    new Guid(index + 1, 0, 0, new byte[8]), Guid.Empty, startedAt, startValue, Photo, plan.Open ? null : startValue + (plan.Tenths / 10m), plan.Open ? null : Photo);
                if (before?.EndValue is not null && plan.Gap != 0)
                {
                    gaps.Add($"{before.RootId}>{segment.RootId}");
                }

                segments.Add((segment, plan.Arrival));
                before = segment;
            }

            return new History([.. segments.OrderBy(s => s.Arrival).Select(s => s.Segment)], gaps);
        });

    [Fact]
    [Trait("TestCase", "TC-P2-FO-008")]
    public void A_gap_is_found_exactly_where_a_start_does_not_continue_the_end_before_it_whatever_the_arrival_order() =>
        Histories.Sample(history =>
            Continuity.Find(history.Arrival).Select(d => d.Key).Should().BeEquivalentTo(history.PlannedGaps));

    [Fact]
    public void The_check_does_not_depend_on_the_order_the_segments_arrive_in() =>
        Gen.Select(Histories, Gen.Int.Array[25]).Sample((history, order) =>
        {
            var shuffled = history.Arrival.Select((s, i) => (s, order[i])).OrderBy(p => p.Item2).Select(p => p.s).ToList();

            Continuity.Find(shuffled).Should().Equal(Continuity.Find(history.Arrival));
        });

    [Fact]
    [Trait("TestCase", "TC-P2-FO-007")]
    public void A_night_start_that_is_not_the_day_end_is_a_gap_linking_both_segments_with_the_difference()
    {
        var day = Segment(Day, 100m, 108m);
        var night = Segment(Day.AddHours(12), 110.5m, null);

        var gap = Continuity.Find([night, day]).Should().ContainSingle().Subject;

        gap.Before.Should().Be(day);
        gap.After.Should().Be(night);
        gap.Key.Should().Be($"{day.RootId}>{night.RootId}");
        gap.EndValue.Should().Be(108m);
        gap.Difference.Should().Be(2.5m);
        Continuity.Find([day, Segment(Day.AddHours(12), 104m, null)]).Single().Difference.Should().Be(-4m, "a meter that went back is a gap too");
    }

    [Fact]
    public void A_start_after_a_segment_that_never_ended_is_not_compared_and_neither_is_a_first_segment()
    {
        var day = Segment(Day, 100m, 108m);
        var forgotten = Segment(Day.AddHours(10), 108m, null);
        var night = Segment(Day.AddHours(12), 300m, 301m);

        Continuity.Find([day, forgotten, night]).Should().BeEmpty();
        Continuity.Find([night]).Should().BeEmpty();
        Continuity.Find([]).Should().BeEmpty();
    }

    [Fact]
    public void Equal_values_written_differently_continue_and_two_starts_at_one_instant_are_ordered_by_entry()
    {
        var day = Segment(Day, 100m, 108.0m);
        Continuity.Find([day, Segment(Day.AddHours(8), 108m, null)]).Should().BeEmpty("108.0 and 108 are the same reading");

        var (first, second) = (Segment(Day.AddHours(8), 108m, 110m), Segment(Day.AddHours(8), 110m, 111m));
        var (low, high) = first.RootId.CompareTo(second.RootId) < 0 ? (first, second) : (second, first);
        // day → first → second continues; day → second → first does not, twice.
        Continuity.Find([high, day, low]).Select(d => d.Key).Should().Equal(
            low == first ? [] : [$"{day.RootId}>{low.RootId}", $"{low.RootId}>{high.RootId}"]);
    }

    private static MeterSegment Segment(DateTimeOffset startedAt, decimal start, decimal? end) =>
        new(Guid.CreateVersion7(), Guid.Empty, startedAt, start, Photo, end, end is null ? null : Photo);
}
