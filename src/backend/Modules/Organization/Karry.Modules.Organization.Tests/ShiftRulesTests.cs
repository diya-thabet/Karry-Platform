using Karry.Modules.Organization.Domain;

namespace Karry.Modules.Organization.Tests;

/// <summary>Shift pattern and non-working day input (US-P1-ORG-02 AC1–AC2): each refusal has its own code.</summary>
[Trait("Story", "US-P1-ORG-02")]
public sealed class ShiftRulesTests
{
    private static readonly string[] Week = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static (string?, string?, string?, string?, IReadOnlyList<string>?) Shift(string code, string start, string end, params string[] days) =>
        (code, $"Shift {code}", start, end, days.Length == 0 ? Week : days);

    [Fact]
    public void Two_twelve_hour_shifts_are_valid_and_normalised()
    {
        var shifts = ShiftRules.Validate([Shift("day", "06:00", "18:00"), Shift("night", "18:00", "06:00")]).Value;

        shifts.Select(s => s.Code).Should().Equal("DAY", "NIGHT");
        shifts[1].WallLength.Should().Be(TimeSpan.FromHours(12), "an end before the start is the next day");
        shifts[0].Days.Should().HaveCount(7).And.StartWith(DayOfWeek.Monday);
        ShiftRules.Snapshot(shifts)[1].Should().BeEquivalentTo(new ShiftSnapshot("NIGHT", "Shift night", "18:00", "06:00", Week));
    }

    [Fact]
    public void A_shift_with_equal_start_and_end_lasts_24_hours()
    {
        ShiftRules.Validate([Shift("FULL", "07:00", "07:00")]).Value.Single().WallLength.Should().Be(TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData("organization.shifts_overlap", "06:00", "18:00", "17:00", "20:00", "mon", "mon")]
    [InlineData("organization.shifts_overlap", "22:00", "06:00", "05:00", "13:00", "sun", "mon")]
    [InlineData("organization.shift_time_invalid", "6:00", "18:00", "18:00", "06:00", "mon", "mon")]
    [InlineData("organization.shift_time_invalid", "06:00", "24:00", "18:00", "06:00", "mon", "mon")]
    [InlineData("organization.shift_days_invalid", "06:00", "18:00", "18:00", "06:00", "lundi", "mon")]
    public void Invalid_pairs_of_shifts_are_refused(string code, string start1, string end1, string start2, string end2, string day1, string day2)
    {
        ShiftRules.Validate([Shift("A", start1, end1, day1), Shift("B", start2, end2, day2)]).Error!.Code.Should().Be(code);
    }

    [Fact]
    public void A_night_shift_on_sunday_and_a_morning_shift_on_monday_do_not_overlap_when_they_meet()
    {
        ShiftRules.Validate([Shift("N", "22:00", "06:00", "sun"), Shift("M", "06:00", "14:00", "mon")]).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Other_refusals_have_their_own_codes()
    {
        ShiftRules.Validate([]).Error!.Code.Should().Be("organization.shifts_invalid");
        ShiftRules.Validate(null).Error!.Code.Should().Be("organization.shifts_invalid");
        ShiftRules.Validate([.. Enumerable.Range(0, 13).Select(i => Shift($"S{i}", "00:00", "00:01", "mon"))]).Error!.Code.Should().Be("organization.shifts_invalid");
        ShiftRules.Validate([Shift("A B", "06:00", "18:00")]).Error!.Code.Should().Be("organization.shift_code_invalid");
        ShiftRules.Validate([Shift("A", "06:00", "12:00", "mon"), Shift("a", "12:00", "18:00", "mon")]).Error!.Code.Should().Be("organization.shift_code_duplicate");
        ShiftRules.Validate([("A", " ", "06:00", "18:00", Week)]).Error!.Code.Should().Be("organization.shift_name_invalid");
        ShiftRules.Validate([("A", "Jour", "06:00", "18:00", [])]).Error!.Code.Should().Be("organization.shift_days_invalid");
        ShiftRules.Validate([("A", "Jour", "06:00", "18:00", ["mon", "Mon"])]).Error!.Code.Should().Be("organization.shift_days_invalid");
    }

    [Theory]
    [InlineData("public-holiday", "Fête de l'indépendance", null)]
    [InlineData("CLOSURE", "Inventaire", null)]
    [InlineData("holiday", "x", "organization.day_kind_invalid")]
    [InlineData("other", " ", "organization.day_name_invalid")]
    public void Non_working_days_have_a_known_kind_and_a_name(string kind, string name, string? code)
    {
        var day = ShiftRules.ValidateDay(kind, name);

        if (code is null)
        {
            day.IsSuccess.Should().BeTrue();
            ShiftRules.KindCode(day.Value.Kind).Should().Be(kind.ToLowerInvariant());
        }
        else
        {
            day.Error!.Code.Should().Be(code);
        }
    }
}
