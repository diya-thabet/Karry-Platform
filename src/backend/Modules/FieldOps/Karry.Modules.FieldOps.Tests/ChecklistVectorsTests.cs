using System.Text.Json;
using Karry.Modules.FieldOps.Domain;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>
/// The server's checklist answer rules give exactly the answers of <c>tests/vectors/checklist-answers.json</c>, the file
/// the field app's tests read too, so the tablet never signs a checklist the server refuses (ADR-0035 §3).
/// </summary>
[Trait("Story", "US-P2-FO-02")]
public sealed class ChecklistVectorsTests
{
    private static readonly JsonElement Vectors =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "tests", "vectors", "checklist-answers.json"))).RootElement;

    public static TheoryData<string> Cases() => [.. Vectors.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Answers_fit_a_version_and_open_a_hold_as_the_vectors_say(string name)
    {
        var vector = Vectors.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var photo = Vectors.GetProperty("photo").GetString()!;
        var items = Vectors.GetProperty("template").EnumerateArray()
            .Select(i => new ChecklistItem(i.GetProperty("code").GetString()!, new Dictionary<string, string> { ["en"] = "x" }, i.GetProperty("critical").GetBoolean(), i.GetProperty("photoOnNotOk").GetBoolean()))
            .ToList();
        var template = ChecklistTemplate.Publish(TimeProvider.System, new TenantId(Guid.CreateVersion7()), "excavator", 1, true, items, null);
        var answers = vector.GetProperty("answers").EnumerateArray()
            .Select(a => new ChecklistAnswer(a[0].GetString()!, a[1].GetString()!, a[2].GetBoolean() ? photo : null))
            .ToList();

        var critical = ChecklistRules.CriticalNotOk(template, answers);

        var expected = vector.GetProperty("critical");
        if (expected.ValueKind == JsonValueKind.Null)
        {
            critical.Should().BeNull();
        }
        else
        {
            critical.Should().Equal(expected.EnumerateArray().Select(c => c.GetString()));
        }
    }
}
