using CsCheck;
using Karry.Modules.FieldOps.Domain;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>
/// The continuity check (US-P2-FO-07, ADR-0036 §4) as a pure function: a gap exactly where a start does not continue the
/// end just before it in shift-time order, unless a meter event in between explains it, whatever order the facts arrive in.
/// </summary>
[Trait("Story", "US-P2-FO-07")]
public sealed class ContinuityTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
    private static readonly string Photo = "sha256:" + new string('a', 64);

    /// <summary>
    /// A meter's history built to plan: each segment continues the previous end, or is moved off it by a planned jump
    /// that a meter event explains (a reset, a replacement, a failure and its repair) or that nothing explains (a gap),
    /// or that an event recorded with the wrong value before does not explain (a gap), or follows a segment that never
    /// ended (nothing due); the segments come in shuffled.
    /// </summary>
    private sealed record History(IReadOnlyList<MeterSegment> Arrival, IReadOnlyList<MeterChange> Changes, IReadOnlySet<string> PlannedGaps);

    /// <summary>How a segment follows the one before: continuous, a bare jump, a jump explained by an event, or by a wrong one.</summary>
    private enum Link
    {
        Continuous,
        Jump,
        Reset,
        Replaced,
        FailedAndRepaired,
        WrongReset,

        /// <summary>A reset to a value in between, then a replacement from it: explained only when read in time order.</summary>
        TwoEvents,
    }

    private sealed record Plan(int Minutes, int Tenths, int Jump, Link Link, bool Open, int Arrival);

    private static readonly Gen<Plan> Plans =
        Gen.Select(Gen.Int[2, 900], Gen.Int[0, 200], Gen.Int[1, 500].Select(Gen.Bool), Gen.Enum<Link>(), Gen.Int[0, 5], Gen.Int)
            .Select((minutes, tenths, jump, link, open, arrival) => new Plan(minutes, tenths, jump.Item2 ? jump.Item1 : -jump.Item1, link, open == 0, arrival));

    private static readonly Gen<History> Histories =
        Gen.Select(Gen.Int[1_000, 100_000], Plans.Array[1, 25]).Select((first, plans) =>
        {
            var segments = new List<(MeterSegment Segment, int Arrival)>();
            var changes = new List<MeterChange>();
            var gaps = new HashSet<string>(StringComparer.Ordinal);
            var startedAt = Day;
            MeterSegment? before = null;
            foreach (var (plan, index) in plans.Select((p, i) => (p, i)))
            {
                startedAt = startedAt.AddMinutes(plan.Minutes);
                var due = before?.EndValue ?? first / 10m;
                var link = before is null ? Link.Continuous : plan.Link;
                var startValue = link == Link.Continuous ? due : Math.Max(0m, due + (plan.Jump / 10m));
                var eventAt = startedAt.AddMinutes(-1);
                switch (link)
                {
                    case Link.Reset or Link.Replaced:
                        changes.Add(new MeterChange(eventAt, link == Link.Reset ? MeterChange.Reset : MeterChange.Replaced, due, startValue));
                        break;
                    case Link.FailedAndRepaired:
                        changes.Add(new MeterChange(eventAt.AddSeconds(-1), MeterChange.Failed, null, null));
                        changes.Add(new MeterChange(eventAt, MeterChange.Repaired, null, startValue));
                        break;
                    case Link.WrongReset:
                        changes.Add(new MeterChange(eventAt, MeterChange.Reset, due + 0.1m, startValue));
                        break;
                    case Link.TwoEvents:
                        changes.Add(new MeterChange(eventAt, MeterChange.Replaced, due + 1m, startValue));
                        changes.Add(new MeterChange(eventAt.AddSeconds(-1), MeterChange.Reset, due, due + 1m));
                        break;
                }

                var segment = new MeterSegment(
                    new Guid(index + 1, 0, 0, new byte[8]), Guid.Empty, startedAt, startValue, Photo, plan.Open ? null : startValue + (plan.Tenths / 10m), plan.Open ? null : Photo);
                var unexplained = link == Link.WrongReset || (link == Link.Jump && startValue != due);
                if (before?.EndValue is not null && unexplained)
                {
                    gaps.Add($"{before.RootId}>{segment.RootId}");
                }

                segments.Add((segment, plan.Arrival));
                before = segment;
            }

            return new History([.. segments.OrderBy(s => s.Arrival).Select(s => s.Segment)], changes, gaps);
        });

    [Fact]
    [Trait("TestCase", "TC-P2-FO-008")]
    [Trait("TestCase", "TC-P2-FO-009")]
    public void A_gap_is_found_exactly_where_a_start_does_not_continue_what_was_due_whatever_the_arrival_order() =>
        Histories.Sample(history =>
            Continuity.Find(history.Arrival, history.Changes).Select(d => d.Key).Should().BeEquivalentTo(history.PlannedGaps));

    [Fact]
    public void The_check_does_not_depend_on_the_order_the_segments_and_events_arrive_in() =>
        Gen.Select(Histories, Gen.Int.Array[25], Gen.Int.Array[50]).Sample((history, order, eventOrder) =>
        {
            var shuffled = history.Arrival.Select((s, i) => (s, order[i])).OrderBy(p => p.Item2).Select(p => p.s).ToList();
            var events = history.Changes.Select((c, i) => (c, eventOrder[i])).OrderBy(p => p.Item2).Select(p => p.c).ToList();

            Continuity.Find(shuffled, events).Should().Equal(Continuity.Find(history.Arrival, history.Changes));
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
        (gap.Expected, gap.Found, gap.Difference).Should().Be((108m, 110.5m, 2.5m));
        Continuity.Find([day, Segment(Day.AddHours(12), 104m, null)]).Single().Difference.Should().Be(-4m, "a meter that went back is a gap too");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-009")]
    public void A_replacement_between_two_segments_explains_the_jump_and_one_with_the_wrong_old_value_does_not()
    {
        var day = Segment(Day, 12_480m, 12_488m);
        var night = Segment(Day.AddHours(12), 3m, null);
        var replaced = new MeterChange(Day.AddHours(10), MeterChange.Replaced, 12_488m, 3m);

        Continuity.Find([day, night], [replaced]).Should().BeEmpty();
        var wrong = Continuity.Find([day, night], [replaced with { ValueBefore = 12_490m }]).Should().ContainSingle().Subject;
        (wrong.Expected, wrong.Found).Should().Be((12_488m, 12_490m), "the day ended at 12,488.0, not the 12,490.0 the replacement says");
        Continuity.Find([day, night], [replaced with { ValueAfter = 2m }]).Single().Should().Match<Discontinuity>(d => d.Expected == 2m && d.Found == 3m);
        Continuity.Find([day, night], [replaced with { OccurredAt = Day.AddHours(13) }]).Should().ContainSingle("an event after the night start explains nothing before it");
        Continuity.Find([day, night], [replaced with { OccurredAt = Day }]).Should().ContainSingle("an event at the day start is before the day ended");
        Continuity.Find([day, night], [replaced with { OccurredAt = Day.AddHours(12) }]).Should().BeEmpty("an event at the night start comes before it");
    }

    [Fact]
    public void Events_between_two_segments_are_read_in_the_order_they_happened_whatever_order_they_arrive_in()
    {
        var day = Segment(Day, 100m, 108m);
        var night = Segment(Day.AddHours(12), 3m, null);
        var reset = new MeterChange(Day.AddHours(9), MeterChange.Reset, 108m, 0m);
        var replaced = new MeterChange(Day.AddHours(10), MeterChange.Replaced, 2m, 3m);

        Continuity.Find([day, night], [replaced, reset]).Single().Should().Match<Discontinuity>(d => d.Expected == 0m && d.Found == 2m,
            "the reset left the meter at 0.0, the replacement says it showed 2.0");
        Continuity.Find([day, night], [replaced with { ValueBefore = 0m }, reset]).Should().BeEmpty();
    }

    [Fact]
    public void A_failure_leaves_nothing_due_until_the_repair_and_an_estimated_segment_is_not_compared()
    {
        var day = Segment(Day, 100m, 108m);
        var night = Segment(Day.AddHours(12), 150m, 151m);
        var failed = new MeterChange(Day.AddHours(9), MeterChange.Failed, null, null);

        Continuity.Find([day, night], [failed]).Should().BeEmpty("nothing is due after a failure");
        Continuity.Find([day, night], [failed, new MeterChange(Day.AddHours(10), MeterChange.Repaired, null, 149m)]).Single().Expected.Should().Be(149m);
        Continuity.Find([day, night with { Estimated = true }]).Should().BeEmpty();
        Continuity.Find([day with { Estimated = true }, night]).Should().BeEmpty();
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
        Continuity.Find([forgotten, night], [new MeterChange(Day.AddHours(11), MeterChange.Reset, 120m, 299m)]).Single().Expected.Should().Be(299m,
            "an event after a segment that never ended says what is due");
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
