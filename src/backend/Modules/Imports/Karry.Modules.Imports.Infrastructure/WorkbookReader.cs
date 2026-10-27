using System.Globalization;
using System.IO.Compression;
using ClosedXML.Excel;
using Karry.BuildingBlocks.Imports;
using Karry.Modules.Imports.Application;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Imports.Infrastructure;

/// <summary>
/// The checks an uploaded workbook passes before ClosedXML opens it (ADR-0027 §6), on the zip itself: an <c>.xlsx</c>
/// package (not <c>.xls</c>, CSV or anything renamed), without macros, ActiveX, embedded objects or external links,
/// and small enough once unpacked (zip bombs).
/// </summary>
public static class WorkbookInspector
{
    public const long MaxUnpackedBytes = 100L * 1024 * 1024;

    /// <summary>Above this unpacked size, a compression ratio over 100 : 1 is refused (real workbooks stay far below).</summary>
    private const long RatioCheckedFrom = 10L * 1024 * 1024;

    private static readonly string[] ActiveParts = ["xl/vbaproject.bin", "xl/activex/", "xl/embeddings/", "xl/externallinks/"];

    public static Result<bool> Check(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length > ImportErrors.MaxBytes)
        {
            return ImportErrors.FileTooLarge;
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            if (!names.Contains("[content_types].xml") || !names.Contains("xl/workbook.xml"))
            {
                return ImportErrors.FileTypeUnsupported;
            }

            if (names.Any(name => ActiveParts.Any(part => name.StartsWith(part, StringComparison.Ordinal))))
            {
                return ImportErrors.FileActiveContent;
            }

            var unpacked = zip.Entries.Sum(e => e.Length);
            var packed = Math.Max(1, zip.Entries.Sum(e => e.CompressedLength));
            if (unpacked > MaxUnpackedBytes || (unpacked > RatioCheckedFrom && unpacked / packed > 100))
            {
                return ImportErrors.FileTooManyRows;
            }
        }
        catch (InvalidDataException)
        {
            return ImportErrors.FileTypeUnsupported;
        }

        return true;
    }
}

/// <summary>
/// Reads the first visible sheet of a checked workbook (ADR-0027): row 1 holds the headers (English or French, any
/// order, a trailing <c>*</c> ignored), the rows below the data. Values become text: numbers invariant, dates ISO,
/// formulas by their cached value (never evaluated, with a warning). Empty rows are skipped.
/// </summary>
public sealed class WorkbookReader : IWorkbookReader
{
    public Result<bool> Check(byte[] file) => WorkbookInspector.Check(file);

    public Result<WorkbookContent> Read(byte[] file, IReadOnlyList<ImportColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var checkedFile = WorkbookInspector.Check(file);
        if (!checkedFile.IsSuccess)
        {
            return checkedFile.Error;
        }

        try
        {
            using var workbook = new XLWorkbook(new MemoryStream(file, writable: false));
            var sheet = workbook.Worksheets.FirstOrDefault(s => s.Visibility == XLWorksheetVisibility.Visible);
            return sheet is null ? ImportErrors.FileUnreadable : ReadSheet(sheet, columns);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException or FormatException
            or NotSupportedException or System.Xml.XmlException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            return ImportErrors.FileUnreadable;
        }
    }

    private static Result<WorkbookContent> ReadSheet(IXLWorksheet sheet, IReadOnlyList<ImportColumn> columns)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastColumn > ImportErrors.MaxColumns || lastRow - 1 > ImportErrors.MaxRows)
        {
            return ImportErrors.FileTooManyRows;
        }

        var issues = new List<ImportIssue>();
        var byHeader = new Dictionary<string, ImportColumn>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            byHeader.TryAdd(Header(column.English), column);
            byHeader.TryAdd(Header(column.French), column);
            byHeader.TryAdd(Header(column.Key), column);
        }

        var positions = new Dictionary<int, ImportColumn>();
        for (var index = 1; index <= lastColumn; index++)
        {
            var cell = sheet.Cell(1, index);
            var text = Text(cell, out _);
            if (text is null)
            {
                continue;
            }

            var reference = cell.Address.ToStringRelative();
            if (!byHeader.TryGetValue(Header(text), out var column))
            {
                issues.Add(new ImportIssue(1, null, reference, "imports.column_unknown", ImportSeverity.Warning, Args("header", text)));
            }
            else if (positions.ContainsValue(column))
            {
                issues.Add(new ImportIssue(1, column.Key, reference, "imports.column_duplicate", ImportSeverity.Error, Args("header", text)));
            }
            else
            {
                positions[index] = column;
            }
        }

        foreach (var missing in columns.Where(c => c.Required && !positions.ContainsValue(c)))
        {
            issues.Add(new ImportIssue(1, missing.Key, null, "imports.column_missing", ImportSeverity.Error, Args("header", missing.English)));
        }

        var rows = new List<ImportRow>();
        for (var number = 2; number <= lastRow; number++)
        {
            var cells = new Dictionary<string, ImportCell>(StringComparer.Ordinal);
            foreach (var (index, column) in positions)
            {
                var cell = sheet.Cell(number, index);
                var text = Text(cell, out var problem);
                var reference = cell.Address.ToStringRelative();
                cells[column.Key] = new ImportCell(reference, text);
                if (problem is not null)
                {
                    issues.Add(new ImportIssue(number, column.Key, reference, problem.Value.Code, problem.Value.Severity));
                }
            }

            if (cells.Values.Any(c => c.Text is not null))
            {
                rows.Add(new ImportRow(number, cells));
            }
        }

        return new WorkbookContent(rows, issues);
    }

    /// <summary>A header as compared: trimmed, lower case, single spaces, without the required-column star.</summary>
    private static string Header(string text) =>
        string.Join(' ', text.Trim().TrimEnd('*').Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? Text(IXLCell cell, out (string Code, ImportSeverity Severity)? problem)
    {
        problem = null;
        var value = cell.Value;
        if (cell.HasFormula)
        {
            // The value Excel saved with the file: a formula is never evaluated here.
            value = cell.CachedValue;
            problem = ("imports.formula_value_used", ImportSeverity.Warning);
        }

        switch (value.Type)
        {
            case XLDataType.Blank:
                return null;
            case XLDataType.Boolean:
                return value.GetBoolean() ? "true" : "false";
            case XLDataType.Number:
                // Through decimal: 15 significant digits, so 0.1 + 0.2 typed in Excel reads as 0.3.
                return ((decimal)value.GetNumber()).ToString(CultureInfo.InvariantCulture);
            case XLDataType.DateTime:
                var at = value.GetDateTime();
                return at.TimeOfDay == TimeSpan.Zero
                    ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : at.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            case XLDataType.TimeSpan:
                return value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture);
            case XLDataType.Error:
                problem = ("imports.cell_error", ImportSeverity.Error);
                return null;
            default:
                var text = value.GetText().Trim();
                return text.Length == 0 ? null : text;
        }
    }

    private static Dictionary<string, string> Args(string name, string value) => new(StringComparer.Ordinal) { [name] = value };
}
