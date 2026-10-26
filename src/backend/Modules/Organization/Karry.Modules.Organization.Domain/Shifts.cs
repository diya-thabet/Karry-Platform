using System.Globalization;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using NodaTime;
using NodaTime.Text;
using NodaTime.TimeZones;

namespace Karry.Modules.Organization.Domain;

/// <summary>
/// One shift of a pattern (US-P1-ORG-02 AC1): a wall-clock start and end on the site's clock and the weekdays it runs.
/// When the end is not after the start, the shift ends the next day ("18:00–06:00"); equal times mean 24 hours.
/// </summary>
public sealed record ShiftDefinition(string Code, string Name, TimeOnly Start, TimeOnly End, IReadOnlyList<DayOfWeek> Days)
{
    /// <summary>Length on the wall clock; the real length of an instance comes from its instants (ADR-0024).</summary>
    public TimeSpan WallLength => End > Start ? End - Start : TimeSpan.FromDays(1) - (Start - End);

    public bool RunsOn(DayOfWeek day) => Days.Contains(day);
}

public readonly record struct ShiftPatternId(Guid Value) : IEntityId<ShiftPatternId>
{
    public static ShiftPatternId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

public readonly record struct NonWorkingDayId(Guid Value) : IEntityId<NonWorkingDayId>
{
    public static NonWorkingDayId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>Why a site does not work on a day (US-P1-ORG-02 AC2).</summary>
public enum NonWorkingDayKind
{
    PublicHoliday = 1,
    Closure = 2,
    Other = 3,
}

/// <summary>A shift as stored in the ledger: plain values, times as "HH:mm", days as "mon".."sun".</summary>
public sealed record ShiftSnapshot(string Code, string Name, string Start, string End, IReadOnlyList<string> Days);

[EventType("organization.shift_pattern_set.v1")]
public sealed record ShiftPatternSet(
    Guid SiteId, DateOnly EffectiveFrom, IReadOnlyList<ShiftSnapshot>? Before, IReadOnlyList<ShiftSnapshot> After, string? Actor) : DomainEvent;

[EventType("organization.shift_pattern_removed.v1")]
public sealed record ShiftPatternRemoved(Guid SiteId, DateOnly EffectiveFrom, IReadOnlyList<ShiftSnapshot> Shifts, string? Actor) : DomainEvent;

[EventType("organization.non_working_day_set.v1")]
public sealed record NonWorkingDaySet(Guid SiteId, DateOnly Date, string Kind, string Name, string? Actor) : DomainEvent;

[EventType("organization.non_working_day_removed.v1")]
public sealed record NonWorkingDayRemoved(Guid SiteId, DateOnly Date, string Kind, string Name, string? Actor) : DomainEvent;

/// <summary>
/// One version of a site's shift pattern (US-P1-ORG-02 AC1, ADR-0024): the shifts that run from <see cref="EffectiveFrom"/>
/// until the next version. A version that has taken effect is never changed, so past instances are never rewritten.
/// </summary>
public sealed class ShiftPattern : AggregateRoot<ShiftPatternId>, ITenantOwned, ISiteScoped
{
    private ShiftPattern(ShiftPatternId id, TenantId tenantId, SiteId siteId, DateOnly effectiveFrom, IReadOnlyList<ShiftDefinition> shifts, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        SiteId = siteId;
        EffectiveFrom = effectiveFrom;
        Shifts = shifts;
        CreatedAt = at;
        ChangedAt = at;
    }

    // EF Core
    private ShiftPattern()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public SiteId SiteId { get; private set; }

    Guid? ISiteScoped.SiteId => SiteId.Value;

    /// <summary>First local date (site clock) the version applies to.</summary>
    public DateOnly EffectiveFrom { get; private set; }

    public IReadOnlyList<ShiftDefinition> Shifts { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public static ShiftPattern Create(TimeProvider clock, TenantId tenant, SiteId site, DateOnly effectiveFrom, IReadOnlyList<ShiftDefinition> shifts, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(shifts);
        var at = clock.GetUtcNow();
        var pattern = new ShiftPattern(EntityId.New<ShiftPatternId>(clock), tenant, site, effectiveFrom, shifts, at);
        pattern.Raise(new ShiftPatternSet(site.Value, effectiveFrom, null, ShiftRules.Snapshot(shifts), actor) { OccurredAt = at });
        return pattern;
    }

    /// <summary>New shifts for a version not yet in effect; false when nothing changed (no ledger entry).</summary>
    public bool Change(TimeProvider clock, IReadOnlyList<ShiftDefinition> shifts, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(shifts);
        var before = ShiftRules.Snapshot(Shifts);
        var after = ShiftRules.Snapshot(shifts);
        if (before.SequenceEqual(after, ShiftRules.SnapshotComparer))
        {
            return false;
        }

        var at = clock.GetUtcNow();
        Shifts = shifts;
        ChangedAt = at;
        Raise(new ShiftPatternSet(SiteId.Value, EffectiveFrom, before, after, actor) { OccurredAt = at });
        return true;
    }

    /// <summary>Records the removal of a version not yet in effect (the store deletes it).</summary>
    public void Remove(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Raise(new ShiftPatternRemoved(SiteId.Value, EffectiveFrom, ShiftRules.Snapshot(Shifts), actor) { OccurredAt = clock.GetUtcNow() });
    }
}

/// <summary>A day a site does not work (US-P1-ORG-02 AC2): no shift starts on it. Read later by the weather engine.</summary>
public sealed class NonWorkingDay : AggregateRoot<NonWorkingDayId>, ITenantOwned, ISiteScoped
{
    private NonWorkingDay(NonWorkingDayId id, TenantId tenantId, SiteId siteId, DateOnly date, NonWorkingDayKind kind, string name, DateTimeOffset at)
        : base(id)
    {
        TenantId = tenantId;
        SiteId = siteId;
        Date = date;
        Kind = kind;
        Name = name;
        ChangedAt = at;
    }

    // EF Core
    private NonWorkingDay()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public SiteId SiteId { get; private set; }

    Guid? ISiteScoped.SiteId => SiteId.Value;

    public DateOnly Date { get; private set; }

    public NonWorkingDayKind Kind { get; private set; }

    public string Name { get; private set; } = null!;

    public DateTimeOffset ChangedAt { get; private set; }

    public static NonWorkingDay Create(TimeProvider clock, TenantId tenant, SiteId site, DateOnly date, NonWorkingDayKind kind, string name, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var at = clock.GetUtcNow();
        var day = new NonWorkingDay(EntityId.New<NonWorkingDayId>(clock), tenant, site, date, kind, name, at);
        day.Raise(new NonWorkingDaySet(site.Value, date, ShiftRules.KindCode(kind), name, actor) { OccurredAt = at });
        return day;
    }

    /// <summary>A new kind or name; false when nothing changed (no ledger entry).</summary>
    public bool Change(TimeProvider clock, NonWorkingDayKind kind, string name, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (kind == Kind && string.Equals(name, Name, StringComparison.Ordinal))
        {
            return false;
        }

        var at = clock.GetUtcNow();
        Kind = kind;
        Name = name;
        ChangedAt = at;
        Raise(new NonWorkingDaySet(SiteId.Value, Date, ShiftRules.KindCode(kind), name, actor) { OccurredAt = at });
        return true;
    }

    /// <summary>Records the removal (the store deletes it).</summary>
    public void Remove(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Raise(new NonWorkingDayRemoved(SiteId.Value, Date, ShiftRules.KindCode(Kind), Name, actor) { OccurredAt = clock.GetUtcNow() });
    }
}

/// <summary>
/// A shift on a date, computed from the rules (ADR-0024): start and end as instants, the local date it starts on (an
/// instance belongs to its start date) and the time zone used.
/// </summary>
public sealed record ComputedShift(DateOnly Date, string Code, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string TimeZone)
{
    public TimeSpan Length => EndsAt - StartsAt;
}

/// <summary>
/// A stored shift instance of the next days (US-P1-ORG-02 AC3), written by the generator and synced to the field app
/// (P1.15). Unique by site, date and shift code; one that has started is never changed or removed.
/// </summary>
public sealed class ShiftInstance : ITenantOwned, ISiteScoped
{
    private ShiftInstance(Guid id, TenantId tenantId, SiteId siteId, ComputedShift shift)
    {
        Id = id;
        TenantId = tenantId;
        SiteId = siteId;
        Date = shift.Date;
        ShiftCode = shift.Code;
        Apply(shift);
    }

    // EF Core
    private ShiftInstance()
    {
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public SiteId SiteId { get; private set; }

    Guid? ISiteScoped.SiteId => SiteId.Value;

    public DateOnly Date { get; private set; }

    public string ShiftCode { get; private set; } = null!;

    public string ShiftName { get; private set; } = null!;

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public string TimeZone { get; private set; } = null!;

    public static ShiftInstance From(TimeProvider clock, TenantId tenant, SiteId site, ComputedShift shift)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(shift);
        return new(Guid.CreateVersion7(clock.GetUtcNow()), tenant, site, shift);
    }

    public bool Matches(ComputedShift shift)
    {
        ArgumentNullException.ThrowIfNull(shift);
        return ShiftName == shift.Name && StartsAt == shift.StartsAt && EndsAt == shift.EndsAt && TimeZone == shift.TimeZone;
    }

    public void Apply(ComputedShift shift)
    {
        ArgumentNullException.ThrowIfNull(shift);
        ShiftName = shift.Name;
        StartsAt = shift.StartsAt;
        EndsAt = shift.EndsAt;
        TimeZone = shift.TimeZone;
    }
}

/// <summary>
/// Shift time (ADR-0024): wall-clock shifts on the site's clock, resolved with NodaTime. A start or end inside a
/// spring-forward gap moves forward by the gap; one inside a fall-back overlap takes the earlier offset.
/// </summary>
public static class ShiftCalendar
{
    /// <summary>Days of stored instances ahead of the site's today (US-P1-ORG-02 AC3).</summary>
    public const int StoredDays = 30;

    /// <summary>Longest range computed on request.</summary>
    public const int MaxComputedDays = 366;

    private static readonly ZoneLocalMappingResolver Resolver = Resolvers.CreateMappingResolver(Resolvers.ReturnEarlier, Resolvers.ReturnForwardShifted);

    /// <summary>The site's local date at an instant.</summary>
    public static DateOnly Today(string timeZone, DateTimeOffset now) =>
        Instant.FromDateTimeOffset(now).InZone(Zone(timeZone)).Date.ToDateOnly();

    /// <summary>
    /// The shifts starting on each local date from <paramref name="from"/> to <paramref name="to"/> (inclusive): the
    /// newest version effective on the date, nothing on a non-working day, ordered by start. A shift that the clock
    /// change makes empty (for example 02:30–03:00 on the night 02:00 becomes 03:00) does not happen that day.
    /// </summary>
    public static IReadOnlyList<ComputedShift> Compute(
        string timeZone, IReadOnlyList<(DateOnly EffectiveFrom, IReadOnlyList<ShiftDefinition> Shifts)> versions,
        IReadOnlySet<DateOnly> nonWorkingDays, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(nonWorkingDays);
        var zone = Zone(timeZone);
        var ordered = versions.OrderByDescending(v => v.EffectiveFrom).ToList();
        var result = new List<ComputedShift>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (nonWorkingDays.Contains(date) || ordered.FirstOrDefault(v => v.EffectiveFrom <= date) is not { Shifts: { } shifts })
            {
                continue;
            }

            foreach (var shift in shifts.Where(s => s.RunsOn(date.DayOfWeek)))
            {
                var startDate = LocalDate.FromDateOnly(date);
                var endDate = shift.End > shift.Start ? startDate : startDate.PlusDays(1);
                var starts = zone.ResolveLocal(startDate.At(LocalTime.FromTimeOnly(shift.Start)), Resolver).ToDateTimeOffset().ToUniversalTime();
                var ends = zone.ResolveLocal(endDate.At(LocalTime.FromTimeOnly(shift.End)), Resolver).ToDateTimeOffset().ToUniversalTime();
                if (ends > starts)
                {
                    result.Add(new ComputedShift(date, shift.Code, shift.Name, starts, ends, timeZone));
                }
            }
        }

        return [.. result.OrderBy(s => s.StartsAt).ThenBy(s => s.Code, StringComparer.Ordinal)];
    }

    private static DateTimeZone Zone(string timeZone) =>
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) ?? throw new ArgumentException($"Unknown time zone '{timeZone}'.", nameof(timeZone));
}

/// <summary>Validation of shift patterns and non-working days (US-P1-ORG-02).</summary>
public static partial class ShiftRules
{
    public const int MaxShifts = 12;
    public const int CodeMaxLength = 16;
    public const int NameMaxLength = 60;
    public const int DayNameMaxLength = 100;

    private static readonly string[] DayCodes = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly LocalTimePattern TimePattern = LocalTimePattern.CreateWithInvariantCulture("HH':'mm");

    public static IEqualityComparer<ShiftSnapshot> SnapshotComparer { get; } = new SnapshotEquality();

    /// <summary>
    /// The shifts of a version: 1–12, unique codes, "HH:mm" times, at least one weekday each, and no two shifts on the
    /// wall clock at the same time in the week (a field entry belongs to exactly one shift).
    /// </summary>
    public static Result<IReadOnlyList<ShiftDefinition>> Validate(IReadOnlyList<(string? Code, string? Name, string? Start, string? End, IReadOnlyList<string>? Days)>? shifts)
    {
        if (shifts is null || shifts.Count is 0 or > MaxShifts)
        {
            return Error.Validation("organization.shifts_invalid", $"A pattern has 1 to {MaxShifts} shifts.");
        }

        var result = new List<ShiftDefinition>();
        foreach (var (rawCode, rawName, rawStart, rawEnd, rawDays) in shifts)
        {
            var code = rawCode?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!CodeFormat().IsMatch(code))
            {
                return Error.Validation("organization.shift_code_invalid", $"A shift code is 1 to {CodeMaxLength} letters, digits or dashes.").With("shift", code);
            }

            if (result.Any(s => s.Code == code))
            {
                return Error.Validation("organization.shift_code_duplicate", "Each shift of a pattern has its own code.").With("shift", code);
            }

            var name = rawName?.Trim() ?? string.Empty;
            if (name.Length is 0 or > NameMaxLength)
            {
                return Error.Validation("organization.shift_name_invalid", $"A shift name is 1 to {NameMaxLength} characters.").With("shift", code);
            }

            if (ParseTime(rawStart) is not { } start || ParseTime(rawEnd) is not { } end)
            {
                return Error.Validation("organization.shift_time_invalid", "Shift times are \"HH:mm\" on the site's clock.").With("shift", code);
            }

            var days = new List<DayOfWeek>();
            foreach (var day in rawDays ?? [])
            {
                var index = Array.IndexOf(DayCodes, day?.Trim().ToLowerInvariant());
                if (index < 0 || days.Contains((DayOfWeek)index))
                {
                    return Error.Validation("organization.shift_days_invalid", "Shift days are distinct \"mon\" to \"sun\".").With("shift", code);
                }

                days.Add((DayOfWeek)index);
            }

            if (days.Count == 0)
            {
                return Error.Validation("organization.shift_days_invalid", "A shift runs on at least one day.").With("shift", code);
            }

            result.Add(new ShiftDefinition(code, name, start, end, [.. days.OrderBy(d => ((int)d + 6) % 7)]));
        }

        if (FirstOverlap(result) is { } overlap)
        {
            return Error.Validation("organization.shifts_overlap", "Two shifts of a pattern cannot run at the same time.")
                .With("shifts", $"{overlap.A},{overlap.B}");
        }

        return result;
    }

    public static Result<(NonWorkingDayKind Kind, string Name)> ValidateDay(string? kind, string? name)
    {
        NonWorkingDayKind? parsed = kind?.Trim().ToLowerInvariant() switch
        {
            "public-holiday" => NonWorkingDayKind.PublicHoliday,
            "closure" => NonWorkingDayKind.Closure,
            "other" => NonWorkingDayKind.Other,
            _ => null,
        };
        if (parsed is not { } value)
        {
            return Error.Validation("organization.day_kind_invalid", "The kind is \"public-holiday\", \"closure\" or \"other\".");
        }

        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > DayNameMaxLength
            ? Error.Validation("organization.day_name_invalid", $"A name is 1 to {DayNameMaxLength} characters.")
            : (value, trimmed);
    }

    public static string KindCode(NonWorkingDayKind kind) => kind switch
    {
        NonWorkingDayKind.PublicHoliday => "public-holiday",
        NonWorkingDayKind.Closure => "closure",
        _ => "other",
    };

    public static string DayCode(DayOfWeek day) => DayCodes[(int)day];

    public static string TimeText(TimeOnly time) => time.ToString("HH':'mm", CultureInfo.InvariantCulture);

    public static IReadOnlyList<ShiftSnapshot> Snapshot(IReadOnlyList<ShiftDefinition> shifts)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        return [.. shifts.Select(s => new ShiftSnapshot(s.Code, s.Name, TimeText(s.Start), TimeText(s.End), [.. s.Days.Select(DayCode)]))];
    }

    private static TimeOnly? ParseTime(string? text) =>
        text is not null && TimePattern.Parse(text.Trim()) is { Success: true } parsed ? parsed.Value.ToTimeOnly() : null;

    // Minutes of the week each shift occupies on the wall clock (Monday 00:00 = 0), wrapping from Sunday to Monday.
    private static (string A, string B)? FirstOverlap(List<ShiftDefinition> shifts)
    {
        const int week = 7 * 24 * 60;
        var spans = new List<(string Code, int From, int To)>();
        foreach (var shift in shifts)
        {
            var length = (int)shift.WallLength.TotalMinutes;
            foreach (var day in shift.Days)
            {
                var from = ((((int)day + 6) % 7) * 24 * 60) + (shift.Start.Hour * 60) + shift.Start.Minute;
                var to = from + length;
                if (to <= week)
                {
                    spans.Add((shift.Code, from, to));
                }
                else
                {
                    spans.Add((shift.Code, from, week));
                    spans.Add((shift.Code, 0, to - week));
                }
            }
        }

        for (var i = 0; i < spans.Count; i++)
        {
            for (var j = i + 1; j < spans.Count; j++)
            {
                if (spans[i].From < spans[j].To && spans[j].From < spans[i].To)
                {
                    return (spans[i].Code, spans[j].Code);
                }
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeFormat();

    private sealed class SnapshotEquality : IEqualityComparer<ShiftSnapshot>
    {
        public bool Equals(ShiftSnapshot? x, ShiftSnapshot? y) =>
            x is not null && y is not null && x.Code == y.Code && x.Name == y.Name && x.Start == y.Start && x.End == y.End && x.Days.SequenceEqual(y.Days);

        public int GetHashCode(ShiftSnapshot obj) => HashCode.Combine(obj?.Code, obj?.Start, obj?.End);
    }
}
