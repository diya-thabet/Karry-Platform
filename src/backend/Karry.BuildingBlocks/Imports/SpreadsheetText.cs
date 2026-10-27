namespace Karry.BuildingBlocks.Imports;

/// <summary>
/// Text written into a spreadsheet or CSV that we produce (templates, later exports): a value that a spreadsheet would
/// read as a formula (starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return) gets a leading
/// apostrophe, so opening the file never runs it (ADR-0027, OWASP CSV injection). Stored data is not changed: this runs
/// on the way out only.
/// </summary>
public static class SpreadsheetText
{
    private static readonly char[] Triggers = ['=', '+', '-', '@', '\t', '\r'];

    public static string Neutralise(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : Array.IndexOf(Triggers, text[0]) >= 0 ? "'" + text : text;
}
