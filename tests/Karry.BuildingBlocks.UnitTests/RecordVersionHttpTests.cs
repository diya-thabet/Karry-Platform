using Karry.BuildingBlocks.Api;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>Hardening plan H-C1: what <c>If-Match</c> an update accepts (RFC 9110 §13.1.1, one tag).</summary>
[Trait("Story", "P1.18a")]
public sealed class RecordVersionHttpTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("*", null)]
    [InlineData("\"4711\"", 4711u)]
    [InlineData(" \"4711\" ", 4711u)]
    [InlineData("W/\"4711\"", 4711u)]
    [InlineData("\"0\"", 0u)]
    [InlineData("\"4294967295\"", uint.MaxValue)]
    public void A_version_tag_or_nothing_is_understood(string? ifMatch, uint? expected)
    {
        var result = RecordVersionHttp.Expected(ifMatch);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("4711")]
    [InlineData("\"\"")]
    [InlineData("\"abc\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"+1\"")]
    [InlineData("\"1 \"")]
    [InlineData("\"4294967296\"")]
    [InlineData("\"1\", \"2\"")]
    public void Anything_else_is_refused(string ifMatch)
    {
        var result = RecordVersionHttp.Expected(ifMatch);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(RecordVersionHttp.InvalidCode);
    }

    [Fact]
    public void The_tag_round_trips() =>
        RecordVersionHttp.Expected(RecordVersionHttp.ETag(123)).Value.Should().Be(123u);
}
