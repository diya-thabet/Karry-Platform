using System.Text.Json.Nodes;
using Karry.Modules.Trust.Application;

namespace Karry.Modules.Trust.Tests;

[Trait("Story", "US-P1-AUD-04")]
public sealed class AuditChangesTests
{
    [Fact]
    public void Objects_are_compared_field_by_field_and_lists_as_a_whole()
    {
        var changes = Changes("""
            {"siteId":"s","before":{"name":"Carrière Nord","address":{"city":"Tunis","zip":"1000"},"roles":["a","b"],"code":"CN"},
                           "after":{"name":"Carrière Nord","address":{"city":"Sfax","zip":"1000"},"roles":["a"],"capacity":12}}
            """);

        changes.Select(c => c.Path).Should().Equal("address.city", "roles", "code", "capacity");
        changes[0].Before!.GetValue<string>().Should().Be("Tunis");
        changes[0].After!.GetValue<string>().Should().Be("Sfax");
        changes[2].After.Should().BeNull("removed");
        changes[3].Before.Should().BeNull("added");
    }

    [Fact]
    public void A_missing_before_lists_the_whole_after_and_plain_values_have_an_empty_path()
    {
        Changes("""{"before":null,"after":{"kg":1.6}}""").Should().ContainSingle().Which.Path.Should().BeEmpty();
        Changes("""{"before":false,"after":true}""").Should().ContainSingle().Which.After!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void The_masked_fields_list_is_not_a_change_and_events_without_both_sides_have_none()
    {
        Changes("""{"before":{"a":1,"maskedFields":["price"]},"after":{"a":1}}""").Should().BeEmpty();
        Changes("""{"after":{"a":1}}""").Should().BeEmpty();
        Changes("""{"from":"active","to":"suspended"}""").Should().BeEmpty();
        AuditChanges.Of(null).Should().BeEmpty();
    }

    [Fact]
    public void Unchanged_values_are_not_a_change() =>
        Changes("""{"before":{"t":1.50,"tags":["x"],"at":{"d":"2026-10-28"}},"after":{"t":1.50,"tags":["x"],"at":{"d":"2026-10-28"}}}""").Should().BeEmpty();

    private static IReadOnlyList<AuditChange> Changes(string json) => AuditChanges.Of(JsonNode.Parse(json)!.AsObject());
}
