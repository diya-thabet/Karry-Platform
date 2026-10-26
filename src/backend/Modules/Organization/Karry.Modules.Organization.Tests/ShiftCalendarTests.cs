using CsCheck;
using Karry.Modules.Organization.Domain;
using NodaTime;

namespace Karry.Modules.Organization.Tests;

/// <summary>
/// Shift time (US-P1-ORG-02, ADR-0024): wall-clock shifts on the site's clock across midnight, clock changes, holidays
/// and pattern versions. Examples first, then the properties of TC-P1-ORG-003 over random zones, dates and patterns.
/// </summary>
[Trait("Story", "US-P1-ORG-02")]
public sealed class ShiftCalendarTests
{
    private static readonly DayOfWeek[] EveryDay = Enum.GetValues<DayOfWeek>();

    private static readonly IReadOnlyList<ShiftDefinition> DayAndNight =
    [
        new("DAY", "Jour", new TimeOnly(6, 0), new TimeOnly(18, 0), EveryDay),
        new("NIGHT", "Nuit", new TimeOnly(18, 0), new TimeOnly(6, 0), EveryDay),
    ];

    private static IReadOnlyList<ComputedShift> Compute(
        string zone, IReadOnlyList<ShiftDefinition> shifts, DateOnly from, DateOnly to, params DateOnly[] holidays) =>
        ShiftCalendar.Compute(zone, [(new DateOnly(2020, 1, 1), shifts)], holidays.ToHashSet(), from, to);

    private static LocalDateTime Local(DateTimeOffset instant, string zone) =>
        Instant.FromDateTimeOffset(instant).InZone(DateTimeZoneProviders.Tzdb[zone]).LocalDateTime;

    [Fact]
    public void A_night_shift_belongs_to_the_date_it_starts_on_and_ends_the_next_morning()
    {
        var shifts = Compute("Africa/Tunis", DayAndNight, new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31));

        shifts.Select(s => s.Code).Should().Equal("DAY", "NIGHT");
        var night = shifts[1];
        night.Date.Should().Be(new DateOnly(2026, 12, 31));
        night.StartsAt.Should().Be(new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.Zero), "Tunis is UTC+1");
        night.EndsAt.Should().Be(new DateTimeOffset(2027, 1, 1, 5, 0, 0, TimeSpan.Zero));
        night.TimeZone.Should().Be("Africa/Tunis");
    }

    [Fact]
    public void A_night_shift_lasts_11_hours_when_clocks_go_forward_and_13_when_they_go_back()
    {
        var spring = Compute("Europe/Paris", DayAndNight, new DateOnly(2026, 3, 28), new DateOnly(2026, 3, 28)).Single(s => s.Code == "NIGHT");
        var autumn = Compute("Europe/Paris", DayAndNight, new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 24)).Single(s => s.Code == "NIGHT");

        spring.Length.Should().Be(TimeSpan.FromHours(11));
        autumn.Length.Should().Be(TimeSpan.FromHours(13));
        Local(spring.EndsAt, "Europe/Paris").TimeOfDay.Should().Be(new LocalTime(6, 0), "the shift ends at 06:00 on the site's clock whatever the date");
        Local(autumn.EndsAt, "Europe/Paris").TimeOfDay.Should().Be(new LocalTime(6, 0));
    }

    [Fact]
    public void A_start_in_the_spring_gap_moves_forward_and_one_in_the_autumn_overlap_takes_the_earlier_offset()
    {
        IReadOnlyList<ShiftDefinition> early = [new("EARLY", "Tôt", new TimeOnly(2, 30), new TimeOnly(10, 30), EveryDay)];

        var spring = Compute("Europe/Paris", early, new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29)).Single();
        var autumn = Compute("Europe/Paris", early, new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25)).Single();

        Local(spring.StartsAt, "Europe/Paris").TimeOfDay.Should().Be(new LocalTime(3, 30), "02:30 does not exist that night: moved forward by the gap");
        spring.Length.Should().Be(TimeSpan.FromHours(7));
        autumn.StartsAt.Should().Be(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), "the first 02:30, still at +02:00");
        autumn.Length.Should().Be(TimeSpan.FromHours(9));
    }

    [Fact]
    public void Where_clocks_change_at_midnight_a_shift_starting_at_midnight_starts_at_one()
    {
        IReadOnlyList<ShiftDefinition> night = [new("N", "Nuit", new TimeOnly(0, 0), new TimeOnly(8, 0), EveryDay)];
        var zone = DateTimeZoneProviders.Tzdb["America/Havana"];
        var transition = zone.GetZoneIntervals(Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 12, 31, 0, 0))
            .First(i => i.Savings != Offset.Zero).IsoLocalStart.Date.ToDateOnly();

        var shift = Compute("America/Havana", night, transition, transition).Single();

        Local(shift.StartsAt, "America/Havana").Should().Be(LocalDate.FromDateOnly(transition).At(new LocalTime(1, 0)));
        shift.Length.Should().Be(TimeSpan.FromHours(7));
    }

    [Fact]
    public void A_non_working_day_has_no_shifts_but_the_night_shift_of_the_day_before_still_ends_on_it()
    {
        var holiday = new DateOnly(2026, 1, 1);

        var shifts = Compute("Africa/Tunis", DayAndNight, new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 2), holiday);

        shifts.Select(s => (s.Date, s.Code)).Should().Equal(
            (new DateOnly(2025, 12, 31), "DAY"), (new DateOnly(2025, 12, 31), "NIGHT"), (new DateOnly(2026, 1, 2), "DAY"), (new DateOnly(2026, 1, 2), "NIGHT"));
    }

    [Fact]
    public void Each_date_follows_the_newest_version_effective_on_it()
    {
        IReadOnlyList<ShiftDefinition> threeEights =
        [
            new("M", "Matin", new TimeOnly(6, 0), new TimeOnly(14, 0), EveryDay),
            new("A", "Après-midi", new TimeOnly(14, 0), new TimeOnly(22, 0), EveryDay),
            new("N", "Nuit", new TimeOnly(22, 0), new TimeOnly(6, 0), EveryDay),
        ];
        var change = new DateOnly(2026, 6, 1);

        var shifts = ShiftCalendar.Compute("Africa/Tunis", [(new DateOnly(2026, 1, 1), DayAndNight), (change, threeEights)], new HashSet<DateOnly>(),
            new DateOnly(2026, 5, 31), new DateOnly(2026, 6, 1));

        shifts.Where(s => s.Date < change).Select(s => s.Code).Should().Equal("DAY", "NIGHT");
        shifts.Where(s => s.Date == change).Select(s => s.Code).Should().Equal("M", "A", "N");
        shifts.Should().NotContain(s => s.Date < new DateOnly(2026, 5, 31));
        ShiftCalendar.Compute("Africa/Tunis", [(change, threeEights)], new HashSet<DateOnly>(), new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31))
            .Should().BeEmpty("no version is effective before the first one");
    }

    [Fact]
    public void Shifts_run_only_on_their_weekdays()
    {
        IReadOnlyList<ShiftDefinition> weekdays = [new("D", "Jour", new TimeOnly(7, 0), new TimeOnly(16, 0), [DayOfWeek.Monday, DayOfWeek.Tuesday])];

        var shifts = Compute("UTC", weekdays, new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1));

        shifts.Select(s => s.Date.DayOfWeek).Should().Equal(DayOfWeek.Monday, DayOfWeek.Tuesday);
    }

    [Fact]
    public void The_site_today_is_its_local_date()
    {
        var instant = new DateTimeOffset(2026, 10, 26, 23, 30, 0, TimeSpan.Zero);

        ShiftCalendar.Today("Africa/Tunis", instant).Should().Be(new DateOnly(2026, 10, 27));
        ShiftCalendar.Today("America/New_York", instant).Should().Be(new DateOnly(2026, 10, 26));
    }

    // --- TC-P1-ORG-003: properties over random zones, dates, patterns and holidays -------------------------------

    private static readonly string[] Zones =
    [
        "Europe/Paris", "America/New_York", "America/Havana", "America/Santiago", "Australia/Lord_Howe", "Pacific/Chatham",
        "Africa/Casablanca", "Africa/Tunis", "Africa/Conakry", "Asia/Kolkata", "UTC",
    ];

    // A day split into 1, 2, 3, 4 or 6 contiguous shifts starting at a random quarter hour, on a random set of weekdays.
    private static readonly Gen<IReadOnlyList<ShiftDefinition>> Pattern =
        Gen.Select(Gen.OneOfConst(1, 2, 3, 4, 6), Gen.Int[0, 95], Gen.Int[1, 127]).Select((count, quarter, dayMask) =>
        {
            var days = EveryDay.Where(d => (dayMask & (1 << (int)d)) != 0).ToList();
            var length = 24 * 60 / count;
            return (IReadOnlyList<ShiftDefinition>)[.. Enumerable.Range(0, count).Select(i =>
            {
                var start = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(((quarter * 15) + (i * length)) % 1440));
                return new ShiftDefinition($"S{i}", $"Shift {i}", start, start.AddMinutes(length), days);
            })];
        });

    private static readonly Gen<(string Zone, IReadOnlyList<ShiftDefinition> Shifts, DateOnly From, DateOnly To, HashSet<DateOnly> Holidays)> Case =
        Gen.Select(Gen.OneOfConst(Zones), Pattern, Gen.Int[0, 20 * 365], Gen.Int[1, 60], Gen.Int[0, 1 << 30]).Select((zone, shifts, offset, days, holidayBits) =>
        {
            var from = new DateOnly(2020, 1, 1).AddDays(offset);
            var to = from.AddDays(days - 1);
            var holidays = Enumerable.Range(0, days).Where(i => (holidayBits >> (i % 30) & 7) == 0).Select(from.AddDays).ToHashSet();
            return (zone, shifts, from, to, holidays);
        });

    [Fact]
    public void Every_working_day_has_each_of_its_shifts_once_and_holidays_none()
    {
        Case.Sample(c =>
        {
            var shifts = ShiftCalendar.Compute(c.Zone, [(c.From, c.Shifts)], c.Holidays, c.From, c.To);
            for (var date = c.From; date <= c.To; date = date.AddDays(1))
            {
                var expected = c.Holidays.Contains(date) ? [] : c.Shifts.Where(s => s.RunsOn(date.DayOfWeek)).Select(s => s.Code).Order().ToList();
                if (!shifts.Where(s => s.Date == date).Select(s => s.Code).Order().SequenceEqual(expected))
                {
                    return false;
                }
            }

            return true;
        });
    }

    [Fact]
    public void Shifts_start_on_their_date_at_their_time_unless_it_falls_in_a_gap_and_never_overlap()
    {
        Case.Sample(c =>
        {
            var zone = DateTimeZoneProviders.Tzdb[c.Zone];
            var shifts = ShiftCalendar.Compute(c.Zone, [(c.From, c.Shifts)], c.Holidays, c.From, c.To);
            foreach (var shift in shifts)
            {
                var definition = c.Shifts.Single(s => s.Code == shift.Code);
                var declared = LocalDate.FromDateOnly(shift.Date).At(LocalTime.FromTimeOnly(definition.Start));
                var start = Local(shift.StartsAt, c.Zone);
                var inGap = zone.MapLocal(declared).Count == 0;
                if (shift.EndsAt <= shift.StartsAt || start.Date != declared.Date || (inGap ? start <= declared : start != declared))
                {
                    return false;
                }
            }

            return shifts.Zip(shifts.Skip(1)).All(pair => pair.First.EndsAt <= pair.Second.StartsAt);
        });
    }

    [Fact]
    public void A_shift_is_as_long_as_on_the_wall_clock_give_or_take_the_clock_change()
    {
        Case.Sample(c =>
        {
            var shifts = ShiftCalendar.Compute(c.Zone, [(c.From, c.Shifts)], c.Holidays, c.From, c.To);
            return shifts.All(s => (s.Length - c.Shifts.Single(d => d.Code == s.Code).WallLength).Duration() <= TimeSpan.FromHours(1));
        });
    }

    [Fact]
    public void Shifts_that_follow_each_other_on_the_wall_clock_follow_each_other_in_time_across_clock_changes()
    {
        Case.Where(c => c.Holidays.Count == 0).Sample(c =>
        {
            var everyDay = c.Shifts.Select(s => s with { Days = EveryDay }).ToList();
            var shifts = ShiftCalendar.Compute(c.Zone, [(c.From, everyDay)], c.Holidays, c.From, c.To);
            return shifts.Zip(shifts.Skip(1)).All(pair => pair.First.EndsAt == pair.Second.StartsAt);
        });
    }
}
