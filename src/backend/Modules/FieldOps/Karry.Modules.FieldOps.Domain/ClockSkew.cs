namespace Karry.Modules.FieldOps.Domain;

/// <summary>A clock skew found on a signed fact: why, and by how many minutes the tablet's clock was ahead (negative: behind).</summary>
public sealed record SkewFound(string Reason, long MinutesAhead);

/// <summary>
/// RULE-FO-03 (ADR-0036 §1, threat T-04): whether a signed fact's device time cannot be trusted. The tablet signs with
/// its own clock (the envelope's <c>context.deviceTime</c>) and sends the offset it last measured against the server
/// (<c>offsetMs</c> = server − tablet, ADR-0031 §6) with the command. Two findings, both "unexplained":
/// <list type="bullet">
/// <item><see cref="KnownOffset"/>: the tablet knew its clock was wrong by more than the tolerance when it signed.</item>
/// <item><see cref="Ahead"/>: the device time, corrected by that offset, is later than the server received the fact; an
/// offline delay only makes a fact older, never newer, so the clock moved forward after it was last measured.</item>
/// </list>
/// A clock moved back while offline looks like an offline delay and cannot be told apart (ADR-0036, as built in 5b).
/// </summary>
public static class ClockSkew
{
    public const string KnownOffset = "offset";

    public const string Ahead = "ahead";

    /// <summary>The largest offset a command may report: beyond a year it is not a clock, it is a broken value.</summary>
    public const long MaxOffsetMs = 366L * 24 * 60 * 60 * 1000;

    public static SkewFound? Judge(DateTimeOffset deviceTime, long offsetMs, DateTimeOffset receivedAt, TimeSpan tolerance)
    {
        var offset = TimeSpan.FromMilliseconds(offsetMs);
        if (offset.Duration() > tolerance)
        {
            return new SkewFound(KnownOffset, Minutes(-offset));
        }

        var ahead = deviceTime + offset - receivedAt;
        return ahead > tolerance ? new SkewFound(Ahead, Minutes(ahead)) : null;
    }

    private static long Minutes(TimeSpan span) => (long)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);
}
