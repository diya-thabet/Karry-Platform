using System.Globalization;
using System.Text.Json;
using Karry.Modules.Organization.Domain;
using Karry.Testing;

namespace Karry.Modules.Organization.Tests;

/// <summary>
/// The server resolves an instant to the shift of <c>tests/vectors/shift-resolution.json</c>, the file the field app's
/// <c>shifts.test.ts</c> reads too, so the shift the tablet preselects is the one the server stores (P2.5, ADR-0034 §2):
/// night shifts across midnight, the 60-minute early start, non-working days, pattern versions, clock changes.
/// </summary>
[Trait("Story", "US-P2-FO-01")]
[Trait("TestCase", "TC-P2-FO-001")]
public sealed class ShiftCalendarVectorsTests
{
    private static readonly JsonElement Vectors =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "tests", "vectors", "shift-resolution.json"))).RootElement;

    private static readonly string[] DayCodes = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    public static TheoryData<string> Names() => [.. Vectors.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(Names))]
    public void An_instant_belongs_to_the_shift_of_the_vectors(string name)
    {
        var c = Vectors.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var holidays = c.GetProperty("nonWorkingDays").EnumerateArray().Select(d => Date(d.GetString()!)).ToHashSet();

        var shift = ShiftCalendar.Resolve(
            c.GetProperty("timeZone").GetString()!, Versions(c.GetProperty("pattern").GetString()!), holidays, Instant(c.GetProperty("at").GetString()!));

        var expected = c.GetProperty("shift");
        if (expected.ValueKind == JsonValueKind.Null)
        {
            shift.Should().BeNull();
            return;
        }

        shift.Should().NotBeNull();
        (shift!.Date, shift.Code, shift.StartsAt, shift.EndsAt).Should().Be((
            Date(expected.GetProperty("date").GetString()!), expected.GetProperty("code").GetString()!,
            Instant(expected.GetProperty("startsAt").GetString()!), Instant(expected.GetProperty("endsAt").GetString()!)));
    }

    private static List<(DateOnly EffectiveFrom, IReadOnlyList<ShiftDefinition> Shifts)> Versions(string pattern) =>
    [
        .. Vectors.GetProperty("patterns").GetProperty(pattern).EnumerateArray().Select(v => (
            Date(v.GetProperty("effectiveFrom").GetString()!),
            (IReadOnlyList<ShiftDefinition>)[.. v.GetProperty("shifts").EnumerateArray().Select(s => new ShiftDefinition(
                s.GetProperty("code").GetString()!, s.GetProperty("name").GetString()!, Time(s.GetProperty("start").GetString()!), Time(s.GetProperty("end").GetString()!),
                [.. s.GetProperty("days").EnumerateArray().Select(d => (DayOfWeek)Array.IndexOf(DayCodes, d.GetString()))]))])),
    ];

    private static DateOnly Date(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeOnly Time(string text) => TimeOnly.ParseExact(text, "HH:mm", CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
}
