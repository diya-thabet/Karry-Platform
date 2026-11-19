namespace Karry.Modules.FieldOps.Domain;

/// <summary>
/// One segment of a meter as the continuity check reads it (ADR-0036 §4): the current version of a shift entry, with
/// the values it started and ended at and the photos that show them. No end while the segment runs. An estimated
/// segment (made while the meter was failed) carries no reading to compare.
/// </summary>
public sealed record MeterSegment(
    Guid RootId, Guid SiteId, DateTimeOffset StartedAt, decimal StartValue, string StartPhoto, decimal? EndValue, string? EndPhoto, bool Estimated = false);

/// <summary>What a meter event means to the check (ADR-0036 §5); the kinds are the Assets codes.</summary>
public sealed record MeterChange(DateTimeOffset OccurredAt, string Kind, decimal? ValueBefore, decimal? ValueAfter)
{
    public const string Replaced = "replaced";
    public const string Reset = "reset";
    public const string Failed = "failed";
    public const string Repaired = "repaired";
}

/// <summary>
/// An unexplained discontinuity (RULE-FO-01) between two segments: the meter showed <see cref="Found"/> where
/// <see cref="Expected"/> was due (the end before, or the value after a meter event in between).
/// </summary>
public sealed record Discontinuity(MeterSegment Before, MeterSegment After, decimal Expected, decimal Found)
{
    /// <summary>Tells the discontinuities of one meter apart: the two segments, in shift-time order.</summary>
    public string Key => $"{Before.RootId}>{After.RootId}";

    /// <summary>How far the meter is from what was due (negative: it went back).</summary>
    public decimal Difference => Found - Expected;
}

/// <summary>
/// The continuity check of one meter (US-P2-FO-07, ADR-0036 §4), a pure function: the segments in shift-time order
/// (start time, then entry ID for two starts at the same instant), each start against the end of the segment just
/// before it, through the meter events between the two starts (after the first, up to and including the second):
/// a replacement or reset must start from the value due and moves it to its value after; a failure leaves nothing due;
/// a repair makes its reading due. A segment still running has no end, so nothing is due after it until an event says
/// so (a missing end is RULE-FO-08's business); an estimated segment is not compared. Evaluated over the whole meter on
/// every start, end and event, a late entry is compared with its real neighbours, whatever order the facts arrived in
/// (TC-P2-FO-008), and an event explains the jump it records (TC-P2-FO-009).
/// </summary>
public static class Continuity
{
    public static IReadOnlyList<Discontinuity> Find(IEnumerable<MeterSegment> segments, IEnumerable<MeterChange>? changes = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var ordered = segments.OrderBy(s => s.StartedAt).ThenBy(s => s.RootId).ToList();
        var events = (changes ?? []).OrderBy(c => c.OccurredAt).ToList();
        var found = new List<Discontinuity>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var (before, after) = (ordered[i - 1], ordered[i]);
            if (before.Estimated || after.Estimated)
            {
                continue;
            }

            if (Gap(before, after, events.Where(e => e.OccurredAt > before.StartedAt && e.OccurredAt <= after.StartedAt)) is { } gap)
            {
                found.Add(gap);
            }
        }

        return found;
    }

    /// <summary>The first value that is not what was due between the two segments, or null when the meter is continuous.</summary>
    private static Discontinuity? Gap(MeterSegment before, MeterSegment after, IEnumerable<MeterChange> between)
    {
        var due = before.EndValue;
        foreach (var change in between)
        {
            switch (change.Kind)
            {
                case MeterChange.Replaced or MeterChange.Reset:
                    if (due is { } value && change.ValueBefore is { } shown && shown != value)
                    {
                        return new Discontinuity(before, after, value, shown);
                    }

                    due = change.ValueAfter;
                    break;
                case MeterChange.Failed:
                    due = null;
                    break;
                case MeterChange.Repaired:
                    due = change.ValueAfter;
                    break;
            }
        }

        return due is { } expected && expected != after.StartValue ? new Discontinuity(before, after, expected, after.StartValue) : null;
    }
}
