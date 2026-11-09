using System.Globalization;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.FieldOps.Domain;

/// <summary>Where an entry is in its life: started (<c>open</c>) or ended (<c>closed</c>).</summary>
public enum ShiftEntryStatus
{
    Open = 1,
    Closed = 2,
}

/// <summary>A stoppage during a segment: why, and for how long (US-P2-FO-04 AC1).</summary>
public sealed record Stoppage(string Reason, int Minutes);

/// <summary>
/// One version of a shift entry (ADR-0034 §3): one operator on one machine for one stretch of a shift, as signed. Rows
/// are append-only: the start writes version 1, the end version 2, a correction the next one; each version holds the
/// whole entry, so the current state is the highest version. Meter values carry their meter and its kind (hours or km),
/// never a bare number.
/// </summary>
public sealed class ShiftEntryVersion : ITenantOwned
{
    public TenantId TenantId { get; init; }

    public Guid RootId { get; init; }

    public int Version { get; init; }

    public ShiftEntryStatus Status { get; init; }

    public Guid SiteId { get; init; }

    public Guid AssetId { get; init; }

    public Guid MeterId { get; init; }

    /// <summary><c>hours</c> or <c>km</c>: the unit of every meter value of the entry.</summary>
    public required string MeterKind { get; init; }

    /// <summary>The person unlocked on the tablet who signed the start (the envelope's signer, T-01).</summary>
    public Guid OperatorId { get; init; }

    /// <summary>1 for the first segment of the machine in the shift, then one more for each handover.</summary>
    public int SegmentNo { get; init; }

    public Guid? PreviousRootId { get; init; }

    /// <summary>The shift as resolved when the start arrived (null when the start is in no shift).</summary>
    public DateOnly? ShiftDate { get; init; }

    public string? ShiftCode { get; init; }

    public DateTimeOffset? ShiftStartsAt { get; init; }

    public DateTimeOffset? ShiftEndsAt { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public decimal StartValue { get; init; }

    public int FuelStartPercent { get; init; }

    public required string StartPhoto { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public decimal? EndValue { get; init; }

    public int? FuelEndPercent { get; init; }

    public string? EndPhoto { get; init; }

    public IReadOnlyList<Stoppage> Stoppages { get; init; } = [];

    public string? Remarks { get; init; }

    /// <summary>Made while the meter is known to be failed (usage estimated; failed-meter mode comes in P2.7).</summary>
    public bool Estimated { get; init; }

    /// <summary>The flags this version raised (ADR-0034 §6); empty when nothing needs a look.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    public Guid EnvelopeId { get; init; }

    public Guid CommandId { get; init; }

    public Guid DeviceId { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>The flags of ADR-0034 §6: stored with the fact, never a reason to refuse it.</summary>
public static class ShiftEntryFlags
{
    /// <summary>The start differs from the last end of the same meter or of the previous segment.</summary>
    public const string Continuity = "RULE-FO-01";

    /// <summary>Δ is negative, or larger than the segment's real length + 0.5 h on an hour meter.</summary>
    public const string Delta = "RULE-FO-02";

    public const string OutsidePattern = "SHIFT_OUTSIDE_PATTERN";

    public const string AssetNotOnSite = "ASSET_NOT_ON_SITE";

    public const string EndBeforeStart = "END_BEFORE_START";

    public const string Overlap = "OVERLAP";
}

/// <summary>The rules of a shift entry's values (ADR-0034 §3, §6), shared by the handlers and the tests.</summary>
public static partial class ShiftEntryRules
{
    public const string RecordType = "fieldops.shift_entry";

    /// <summary>The largest value a meter shows (as in Assets).</summary>
    public const decimal MaxMeterValue = 9_999_999.9m;

    /// <summary>How much an hour meter may run beyond the segment's real length (RULE-FO-02).</summary>
    public static readonly TimeSpan DeltaTolerance = TimeSpan.FromMinutes(30);

    public const int MaxRemarks = 1000;

    public const int MaxStoppages = 20;

    /// <summary>The stoppage reasons the tablet offers (US-P2-FO-04 AC1); a tenant catalogue comes later.</summary>
    public static readonly IReadOnlySet<string> StoppageReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "breakdown", "weather", "no_material", "no_operator", "other",
    };

    /// <summary>
    /// A meter value as written on the wire: plain decimal text with at most one decimal (<c>"12482.5"</c>), no sign,
    /// no exponent, no leading zero, never a JSON number (no float on the way).
    /// </summary>
    public static bool TryParseMeterValue(string? text, out decimal value)
    {
        value = 0;
        return text is not null && MeterValue().IsMatch(text)
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value <= MaxMeterValue;
    }

    /// <summary>The flags of an end (ADR-0034 §6): end not after start; Δ negative or, for hours, beyond the real length.</summary>
    public static IReadOnlyList<string> EndFlags(string meterKind, DateTimeOffset startedAt, decimal startValue, DateTimeOffset endedAt, decimal endValue)
    {
        var flags = new List<string>();
        if (endedAt <= startedAt)
        {
            flags.Add(ShiftEntryFlags.EndBeforeStart);
        }

        var delta = endValue - startValue;
        var tooLarge = meterKind == "hours" && endedAt > startedAt && delta > (decimal)(endedAt - startedAt + DeltaTolerance).TotalHours;
        if (delta < 0 || tooLarge)
        {
            flags.Add(ShiftEntryFlags.Delta);
        }

        return flags;
    }

    /// <summary>
    /// The flag of a start (RULE-FO-01): the value differs from the end it should continue (the previous segment's end
    /// in a handover, otherwise the meter's last end before the start). Nothing to compare with, nothing to flag.
    /// </summary>
    public static IReadOnlyList<string> StartFlags(decimal? continuesFrom, decimal startValue) =>
        continuesFrom is { } end && end != startValue ? [ShiftEntryFlags.Continuity] : [];

    /// <summary>
    /// Whether two segments share time (OVERLAP, TC-P2-FO-011): <c>[start, end)</c>, a segment without an end still runs.
    /// A segment that ends at or before its start (END_BEFORE_START) holds no time and overlaps nothing.
    /// </summary>
    public static bool Overlaps(DateTimeOffset aStart, DateTimeOffset? aEnd, DateTimeOffset bStart, DateTimeOffset? bEnd)
    {
        if (aEnd <= aStart || bEnd <= bStart)
        {
            return false;
        }

        return aStart < (bEnd ?? DateTimeOffset.MaxValue) && bStart < (aEnd ?? DateTimeOffset.MaxValue);
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,6})(\.[0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex MeterValue();
}
