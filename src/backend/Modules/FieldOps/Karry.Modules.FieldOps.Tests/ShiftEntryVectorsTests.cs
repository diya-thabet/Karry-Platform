using System.Globalization;
using System.Text.Json;
using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Karry.Testing;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>
/// TC-P2-FO-012: the server's shift entry rules give exactly the answers of <c>tests/vectors/shift-entry-rules.json</c>,
/// the file the field app's tests read too (<c>shiftRules.test.ts</c>), so the tablet and the server never disagree on
/// a record's shape, a meter value, a time, Δ, continuity or an overlap.
/// </summary>
[Trait("TestCase", "TC-P2-FO-012")]
public sealed class ShiftEntryVectorsTests
{
    private static readonly JsonElement Vectors =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "tests", "vectors", "shift-entry-rules.json"))).RootElement;

    public static TheoryData<string> Names(string section, string? kind = null)
    {
        var items = kind is null ? Vectors.GetProperty(section) : Vectors.GetProperty("records").GetProperty(section).GetProperty(kind);
        return [.. items.EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];
    }

    [Fact]
    public void Meter_values_read_as_the_vectors_say()
    {
        foreach (var valid in Vectors.GetProperty("meterValues").GetProperty("valid").EnumerateArray())
        {
            ShiftEntryRules.TryParseMeterValue(valid.GetProperty("text").GetString(), out var value).Should().BeTrue(valid.GetProperty("text").GetString());
            (value * 10).Should().Be(valid.GetProperty("tenths").GetInt64());
        }

        foreach (var invalid in Vectors.GetProperty("meterValues").GetProperty("invalid").EnumerateArray())
        {
            ShiftEntryRules.TryParseMeterValue(invalid.GetString(), out _).Should().BeFalse($"'{invalid.GetString()}'");
        }
    }

    [Fact]
    public void Instants_read_as_the_vectors_say()
    {
        foreach (var (section, expected) in new[] { ("valid", true), ("invalid", false) })
        {
            foreach (var text in Vectors.GetProperty("instants").GetProperty(section).EnumerateArray())
            {
                using var record = JsonDocument.Parse(JsonSerializer.Serialize(new { t = text.GetString() }));
                RecordFields.Instant(record.RootElement, "t", out _).Should().Be(expected, text.GetString());
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names), "endFlags", null)]
    public void An_end_raises_the_flags_of_the_vectors(string name)
    {
        var v = Find(Vectors.GetProperty("endFlags"), name);

        var flags = ShiftEntryRules.EndFlags(v.GetProperty("meterKind").GetString()!, Instant(v, "startedAt"), Value(v, "startValue"), Instant(v, "endedAt"), Value(v, "endValue"));

        flags.Should().Equal(Strings(v.GetProperty("flags")));
    }

    [Theory]
    [MemberData(nameof(Names), "startFlags", null)]
    public void A_start_raises_the_flags_of_the_vectors(string name)
    {
        var v = Find(Vectors.GetProperty("startFlags"), name);
        decimal? continuesFrom = v.GetProperty("continuesFrom").ValueKind == JsonValueKind.Null ? null : Value(v, "continuesFrom");

        ShiftEntryRules.StartFlags(continuesFrom, Value(v, "startValue")).Should().Equal(Strings(v.GetProperty("flags")));
    }

    [Theory]
    [MemberData(nameof(Names), "overlaps", null)]
    public void Segments_overlap_as_the_vectors_say_in_either_order(string name)
    {
        var v = Find(Vectors.GetProperty("overlaps"), name);
        var (a, b) = (v.GetProperty("a"), v.GetProperty("b"));

        ShiftEntryRules.Overlaps(Instant(a, "start"), OptionalInstant(a, "end"), Instant(b, "start"), OptionalInstant(b, "end")).Should().Be(v.GetProperty("overlaps").GetBoolean());
        ShiftEntryRules.Overlaps(Instant(b, "start"), OptionalInstant(b, "end"), Instant(a, "start"), OptionalInstant(a, "end")).Should().Be(v.GetProperty("overlaps").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(Names), "start", "valid")]
    public void A_valid_start_record_is_read(string name) =>
        StartShiftEntry.ReadRecord(Record("start", "valid", name)).Should().NotBeNull();

    [Theory]
    [MemberData(nameof(Names), "start", "invalid")]
    public void An_invalid_start_record_is_refused(string name) =>
        StartShiftEntry.ReadRecord(Record("start", "invalid", name)).Should().BeNull();

    [Theory]
    [MemberData(nameof(Names), "end", "valid")]
    public void A_valid_end_record_is_read(string name) =>
        EndShiftEntry.ReadRecord(Record("end", "valid", name)).Should().NotBeNull();

    [Theory]
    [MemberData(nameof(Names), "end", "invalid")]
    public void An_invalid_end_record_is_refused(string name) =>
        EndShiftEntry.ReadRecord(Record("end", "invalid", name)).Should().BeNull();

    private static JsonElement Record(string kind, string section, string name) =>
        Find(Vectors.GetProperty("records").GetProperty(kind).GetProperty(section), name).GetProperty("record");

    private static JsonElement Find(JsonElement list, string name) => list.EnumerateArray().Single(i => i.GetProperty("name").GetString() == name);

    private static string[] Strings(JsonElement list) => [.. list.EnumerateArray().Select(f => f.GetString()!)];

    private static decimal Value(JsonElement v, string name) => decimal.Parse(v.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(JsonElement v, string name) => DateTimeOffset.Parse(v.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private static DateTimeOffset? OptionalInstant(JsonElement v, string name) =>
        v.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Instant(v, name);
}
