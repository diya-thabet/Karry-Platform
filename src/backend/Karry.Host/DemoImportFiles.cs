using ClosedXML.Excel;
using Karry.Modules.Assets.Application;

namespace Karry.Host;

/// <summary>
/// The two Excel files of the P1.14 demo scenario (US-P1-ORG-03): 60 machines of demo company A with 8 mistakes, and the
/// same file fixed (<see cref="DemoFleetFile"/>). Upload the first in the portal (Settings → Import from Excel →
/// Machines) to see each mistake on its cell, then the second to commit. Writes files only; nothing reads a database.
/// </summary>
public static class DemoImportFiles
{
    public const string WithMistakes = "karry-demo-engins-a-corriger.xlsx";

    public const string Fixed = "karry-demo-engins-corrige.xlsx";

    /// <summary>Writes both files into <paramref name="directory"/> (created when missing) and returns their paths.</summary>
    public static IReadOnlyList<string> Write(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        foreach (var (name, withMistakes) in new[] { (WithMistakes, true), (Fixed, false) })
        {
            // The directory is the operator's own command-line argument and the file names are constants: no request data.
            var path = Path.Combine(Path.GetFullPath(directory), name);
            File.WriteAllBytes(path, Workbook(withMistakes)); // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine
            written.Add(path);
        }

        return written;
    }

    /// <summary>The demo fleet as a workbook: the template's data sheet name and headers (French), one machine per row.</summary>
    public static byte[] Workbook(bool withMistakes)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Données");
        for (var c = 0; c < DemoFleetFile.Headers.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = DemoFleetFile.Headers[c];
        }

        var rows = DemoFleetFile.Rows(withMistakes);
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Count; c++)
            {
                if (rows[r][c] is { } value)
                {
                    // Typed as text, as a person types into the template's text cells; the reader parses numbers and dates.
                    sheet.Cell(r + 2, c + 1).SetValue(value);
                }
            }
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
}
