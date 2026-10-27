using ClosedXML.Excel;
using Karry.BuildingBlocks.Imports;
using Karry.Modules.Imports.Application;

namespace Karry.Modules.Imports.Infrastructure;

/// <summary>
/// A kind's template (US-P1-ORG-03 AC1, ADR-0027 §8): the data sheet first (headers in the chosen language, required ones
/// marked <c>*</c>, text columns formatted as text so Excel keeps codes as typed, drop-downs from the lists), then the
/// instructions (each column, whether it is required, an example, its list) and the hidden lists. Every text we write
/// goes through <see cref="SpreadsheetText.Neutralise"/>, since the lists carry the company's own values.
/// </summary>
public sealed class TemplateWriter : ITemplateWriter
{
    private static readonly Dictionary<string, string[]> Words = new(StringComparer.Ordinal)
    {
        ["fr"] = ["Données", "Instructions", "Listes", "Colonne", "Obligatoire", "Exemple", "Valeurs", "oui", "non", "liste déroulante"],
        ["en"] = ["Data", "Instructions", "Lists", "Column", "Required", "Example", "Values", "yes", "no", "drop-down list"],
    };

    public byte[] Write(IReadOnlyList<ImportColumn> columns, string language, IReadOnlyDictionary<string, IReadOnlyList<string>> lists)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(lists);
        var words = Words[language];
        using var workbook = new XLWorkbook();
        var data = workbook.AddWorksheet(words[0]);
        var instructions = workbook.AddWorksheet(words[1]);
        var listSheet = workbook.AddWorksheet(words[2]);
        listSheet.Visibility = XLWorksheetVisibility.Hidden;

        var listRanges = new Dictionary<string, IXLRange>(StringComparer.Ordinal);
        var listColumn = 1;
        foreach (var (name, values) in lists.Where(l => l.Value.Count > 0).OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            listSheet.Cell(1, listColumn).Value = SpreadsheetText.Neutralise(name);
            for (var i = 0; i < values.Count; i++)
            {
                listSheet.Cell(i + 2, listColumn).Value = SpreadsheetText.Neutralise(values[i]);
            }

            listRanges[name] = listSheet.Range(2, listColumn, values.Count + 1, listColumn);
            listColumn++;
        }

        instructions.Cell(1, 1).Value = words[3];
        instructions.Cell(1, 2).Value = words[4];
        instructions.Cell(1, 3).Value = words[5];
        instructions.Cell(1, 4).Value = words[6];
        instructions.Row(1).Style.Font.Bold = true;

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var header = column.HeaderIn(language);
            var cell = data.Cell(1, index + 1);
            cell.Value = SpreadsheetText.Neutralise(column.Required ? $"{header} *" : header);
            var body = data.Range(2, index + 1, ImportErrors.MaxRows + 1, index + 1);
            body.Style.NumberFormat.Format = column.Type switch
            {
                ImportColumnType.Text => "@",
                ImportColumnType.Date => "yyyy-mm-dd",
                _ => "General",
            };
            data.Column(index + 1).Width = Math.Max(14, header.Length + 4);

            IXLRange? range = null;
            var listed = column.List is not null && listRanges.TryGetValue(column.List, out range);
            if (listed)
            {
                var validation = body.CreateDataValidation();
                validation.List(range!, true);
                // A warning, not a block: a value missing from the list (a site created later) can still be typed.
                validation.ErrorStyle = XLErrorStyle.Warning;
            }

            instructions.Cell(index + 2, 1).Value = SpreadsheetText.Neutralise(header);
            instructions.Cell(index + 2, 2).Value = column.Required ? words[7] : words[8];
            instructions.Cell(index + 2, 3).Value = SpreadsheetText.Neutralise(column.Example);
            instructions.Cell(index + 2, 4).Value = listed ? words[9] : string.Empty;
        }

        var headers = data.Range(1, 1, 1, Math.Max(1, columns.Count));
        headers.Style.Font.Bold = true;
        headers.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        data.SheetView.FreezeRows(1);
        instructions.Column(1).Width = 30;
        instructions.Column(2).Width = 14;
        instructions.Column(3).Width = 30;
        instructions.Column(4).Width = 20;
        data.SetTabActive();

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
}
