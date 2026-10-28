using ClosedXML.Excel;
using Karry.Modules.Assets.Application;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// The P1.14 demo files (<c>Karry.Host demo-import-files</c>): both carry the demo fleet as the assets template reads it,
/// the first with the planted mistakes. What the import makes of them is proven in the Assets module tests.
/// </summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class DemoImportFilesTests
{
    [Fact]
    public void Both_files_are_written_with_the_template_headers_and_the_60_machines_the_first_with_its_mistakes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"karry-demo-{Guid.NewGuid():N}");
        try
        {
            var written = DemoImportFiles.Write(directory);

            written.Select(Path.GetFileName).Should().Equal(DemoImportFiles.WithMistakes, DemoImportFiles.Fixed);
            Cells(written[0]).Should().BeEquivalentTo(Expected(withMistakes: true), o => o.WithStrictOrdering());
            Cells(written[1]).Should().BeEquivalentTo(Expected(withMistakes: false), o => o.WithStrictOrdering());
            Cells(written[0])[4][1].Should().BeNull("row 5 has its name forgotten");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static List<List<string?>> Expected(bool withMistakes) =>
        [[.. DemoFleetFile.Headers], .. DemoFleetFile.Rows(withMistakes).Select(r => r.ToList())];

    private static List<List<string?>> Cells(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("Données");
        var columns = DemoFleetFile.Headers.Count;
        return [.. Enumerable.Range(1, sheet.LastRowUsed()!.RowNumber())
            .Select(r => Enumerable.Range(1, columns).Select(c => sheet.Cell(r, c).IsEmpty() ? null : sheet.Cell(r, c).GetString()).ToList())];
    }
}
