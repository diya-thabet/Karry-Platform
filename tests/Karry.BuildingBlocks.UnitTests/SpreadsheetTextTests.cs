using Karry.BuildingBlocks.Imports;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>Formula injection is neutralised in every spreadsheet we write (ADR-0027 §7, P1.14 security test).</summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class SpreadsheetTextTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "'=HYPERLINK(\"http://evil\")")]
    [InlineData("+33 6 12", "'+33 6 12")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\t=1", "'\t=1")]
    [InlineData("\r=1", "'\r=1")]
    public void Text_a_spreadsheet_would_run_gets_a_leading_apostrophe(string text, string expected) =>
        SpreadsheetText.Neutralise(text).Should().Be(expected);

    [Theory]
    [InlineData("CAR-NORD")]
    [InlineData("Carrière = Nord")]
    [InlineData("0/31.5")]
    [InlineData("'déjà protégé")]
    public void Other_text_is_written_as_it_is(string text) => SpreadsheetText.Neutralise(text).Should().Be(text);

    [Fact]
    public void Nothing_becomes_an_empty_cell() => SpreadsheetText.Neutralise(null).Should().BeEmpty();
}
