namespace Karry.Modules.FieldOps.Domain;

/// <summary>
/// One segment of a meter as the continuity check reads it (ADR-0036 §4): the current version of a shift entry, with
/// the values it started and ended at and the photos that show them. No end while the segment runs.
/// </summary>
public sealed record MeterSegment(Guid RootId, Guid SiteId, DateTimeOffset StartedAt, decimal StartValue, string StartPhoto, decimal? EndValue, string? EndPhoto);

/// <summary>An unexplained discontinuity (RULE-FO-01): <see cref="After"/> did not start where <see cref="Before"/> ended.</summary>
public sealed record Discontinuity(MeterSegment Before, MeterSegment After, decimal EndValue)
{
    /// <summary>Tells the discontinuities of one meter apart: the two segments, in shift-time order.</summary>
    public string Key => $"{Before.RootId}>{After.RootId}";

    /// <summary>How far the next start is from the end it should continue (negative: the meter went back).</summary>
    public decimal Difference => After.StartValue - EndValue;
}

/// <summary>
/// The continuity check of one meter (US-P2-FO-07, ADR-0036 §4), a pure function: the segments in shift-time order
/// (start time, then entry ID for two starts at the same instant), each start against the end of the segment just
/// before it. A segment still running has no end to compare with, so the next start is not compared (a missing end is
/// RULE-FO-08's business). Evaluated over the whole meter on every start and end, a late entry is compared with its real
/// neighbours, whatever order the facts arrived in (TC-P2-FO-008). Meter events (P2.7 slice 3) will explain a gap.
/// </summary>
public static class Continuity
{
    public static IReadOnlyList<Discontinuity> Find(IEnumerable<MeterSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var ordered = segments.OrderBy(s => s.StartedAt).ThenBy(s => s.RootId).ToList();
        var found = new List<Discontinuity>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var (before, after) = (ordered[i - 1], ordered[i]);
            if (before.EndValue is { } end && end != after.StartValue)
            {
                found.Add(new Discontinuity(before, after, end));
            }
        }

        return found;
    }
}
