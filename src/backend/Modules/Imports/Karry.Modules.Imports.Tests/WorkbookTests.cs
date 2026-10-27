using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using Karry.BuildingBlocks.Imports;
using Karry.Modules.Imports.Application;
using Karry.Modules.Imports.Infrastructure;

namespace Karry.Modules.Imports.Tests;

/// <summary>
/// Reading uploaded workbooks and writing templates (US-P1-ORG-03 AC1–AC2, ADR-0027 §6–§8): headers in either language,
/// values as invariant text with cell references, and the files that are refused before anything reads them.
/// </summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class WorkbookTests
{
    private static readonly ImportColumn[] Columns =
    [
        new("code", "Code", "Code", Required: true),
        new("name", "Name", "Nom", Required: true),
        new("hours", "Hours", "Heures", Required: false, ImportColumnType.Number),
        new("acquiredOn", "Acquired on", "Date d'acquisition", Required: false, ImportColumnType.Date),
        new("site", "Site", "Site", Required: false, List: "sites", Example: "CAR-NORD"),
    ];

    private readonly WorkbookReader reader = new();

    [Fact]
    public void Reads_french_headers_in_any_order_values_as_invariant_text_with_cell_references_and_skips_empty_rows()
    {
        var file = Workbook(sheet =>
        {
            Headers(sheet, "Heures", "nom *", "CODE", "Date d'acquisition");
            sheet.Cell("A2").Value = 1234.5;
            sheet.Cell("B2").Value = "  Pelle 336 ";
            sheet.Cell("C2").Value = "PEL-01";
            sheet.Cell("D2").Value = new DateTime(2021, 3, 15);
            sheet.Cell("C4").Value = "PEL-02";
            sheet.Cell("B4").Value = "Chargeuse";
            sheet.Cell("A4").Value = 0.1 + 0.2;
        });

        var content = reader.Read(file, Columns).Value;

        content.Issues.Should().BeEmpty();
        content.Rows.Select(r => r.Number).Should().Equal(2, 4);
        var first = content.Rows[0];
        (first["code"], first["name"], first["hours"], first["acquiredOn"], first["site"]).Should().Be(("PEL-01", "Pelle 336", "1234.5", "2021-03-15", null));
        first.Reference("hours").Should().Be("A2");
        first.Reference("code").Should().Be("C2");
        content.Rows[1]["hours"].Should().Be("0.3", "15 significant digits, as Excel shows it");
    }

    [Fact]
    public void Reports_unknown_duplicate_and_missing_columns_on_row_one()
    {
        var file = Workbook(sheet =>
        {
            Headers(sheet, "Code", "Colour", "code");
            sheet.Cell("A2").Value = "PEL-01";
        });

        var issues = reader.Read(file, Columns).Value.Issues;

        issues.Select(i => (i.Row, i.Cell, i.Code, i.Severity)).Should().BeEquivalentTo(
        [
            (1, (string?)"B1", "imports.column_unknown", ImportSeverity.Warning),
            (1, (string?)"C1", "imports.column_duplicate", ImportSeverity.Error),
            (1, (string?)null, "imports.column_missing", ImportSeverity.Error),
        ]);
        issues.Single(i => i.Code == "imports.column_missing").Column.Should().Be("name");
    }

    [Fact]
    public void A_formula_is_read_by_its_saved_value_never_evaluated_and_a_cell_error_is_an_error()
    {
        var file = Workbook(sheet =>
        {
            Headers(sheet, "Code", "Name", "Hours");
            sheet.Cell("A2").Value = "PEL-01";
            sheet.Cell("B2").FormulaA1 = "\"Pelle\"&\" 336\"";
            sheet.Cell("C2").FormulaA1 = "1/0";
        });

        var content = reader.Read(file, Columns).Value;

        content.Rows[0]["name"].Should().Be("Pelle 336", "ClosedXML saved the computed value with the file");
        content.Issues.Select(i => (i.Cell, i.Code, i.Severity)).Should().BeEquivalentTo(
        [
            ((string?)"B2", "imports.formula_value_used", ImportSeverity.Warning),
            ((string?)"C2", "imports.cell_error", ImportSeverity.Error), // the error replaces the formula warning
        ]);
    }

    [Fact]
    public void Anything_but_a_plain_xlsx_is_refused_by_its_content_not_its_name()
    {
        reader.Read("code;name\nPEL-01;Pelle"u8.ToArray(), Columns).Error!.Code.Should().Be("imports.file_type_unsupported", "a CSV");
        reader.Read([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0], Columns).Error!.Code.Should().Be("imports.file_type_unsupported", "an old .xls");
        reader.Read(Zip(("word/document.xml", "<w/>"), ("[Content_Types].xml", "<Types/>")), Columns).Error!.Code
            .Should().Be("imports.file_type_unsupported", "a Word document");
        reader.Read(new byte[ImportErrors.MaxBytes + 1], Columns).Error!.Code.Should().Be("imports.file_too_large");
    }

    [Theory]
    [InlineData("xl/vbaProject.bin")]
    [InlineData("xl/activeX/activeX1.xml")]
    [InlineData("xl/embeddings/oleObject1.bin")]
    [InlineData("xl/externalLinks/externalLink1.xml")]
    public void A_workbook_with_macros_activex_embedded_objects_or_external_links_is_refused(string part)
    {
        var file = WithEntry(Workbook(sheet => Headers(sheet, "Code", "Name")), part, "x");

        reader.Read(file, Columns).Error!.Code.Should().Be("imports.file_active_content");
    }

    [Fact]
    public void A_zip_bomb_is_refused_before_it_is_unpacked()
    {
        // 12 MB of zeros pack into a few kB: over 100 : 1 once past 10 MB.
        var file = WithEntry(Workbook(sheet => Headers(sheet, "Code", "Name")), "xl/media/padding.bin", new string('0', 12 * 1024 * 1024));

        reader.Read(file, Columns).Error!.Code.Should().Be("imports.file_too_many_rows");
    }

    [Fact]
    public void More_than_5000_data_rows_are_refused()
    {
        var file = Workbook(sheet =>
        {
            Headers(sheet, "Code", "Name");
            sheet.Cell(ImportErrors.MaxRows + 2, 1).Value = "TOO-FAR";
        });

        reader.Read(file, Columns).Error!.Code.Should().Be("imports.file_too_many_rows");
    }

    [Fact]
    public void A_template_has_the_data_sheet_first_with_marked_headers_text_columns_drop_downs_and_neutralised_lists()
    {
        var file = new TemplateWriter().Write(Columns, "fr", new Dictionary<string, IReadOnlyList<string>> { ["sites"] = ["CAR-NORD", "=EVIL()"] });

        using var workbook = new XLWorkbook(new MemoryStream(file));
        var data = workbook.Worksheet(1);
        data.Name.Should().Be("Données");
        data.Row(1).CellsUsed().Select(c => c.GetText()).Should().Equal("Code *", "Nom *", "Heures", "Date d'acquisition", "Site");
        data.Cell("A2").Style.NumberFormat.Format.Should().Be("@", "codes stay text, never dates");
        data.Cell("D2").Style.NumberFormat.Format.Should().Be("yyyy-mm-dd");
        data.DataValidations.Should().ContainSingle().Which.Ranges.Single().RangeAddress.ToString().Should().Be("E2:E5001");

        var lists = workbook.Worksheet("Listes");
        lists.Visibility.Should().Be(XLWorksheetVisibility.Hidden);
        // ClosedXML writes the apostrophe as Excel's quote prefix: the cell is text that Excel never runs as a formula.
        var evil = lists.Cell("A3");
        (evil.GetText(), evil.HasFormula, evil.Style.IncludeQuotePrefix).Should().Be(("=EVIL()", false, true));
        workbook.Worksheet("Instructions").Cell("C6").GetText().Should().Be("CAR-NORD");

        // A filled-in template reads back, whatever its language.
        data.Cell("A2").Value = "PEL-01";
        data.Cell("B2").Value = "Pelle";
        using var filled = new MemoryStream();
        workbook.SaveAs(filled);
        var read = reader.Read(filled.ToArray(), Columns).Value;
        read.Issues.Should().BeEmpty();
        read.Rows.Should().ContainSingle().Which["code"].Should().Be("PEL-01");
    }

    private static void Headers(IXLWorksheet sheet, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }
    }

    private static byte[] Workbook(Action<IXLWorksheet> fill)
    {
        using var workbook = new XLWorkbook();
        fill(workbook.AddWorksheet("Data"));
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private static byte[] WithEntry(byte[] file, string name, string content)
    {
        using var output = new MemoryStream();
        output.Write(file);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.SmallestSize).Open(), Encoding.ASCII);
            writer.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return output.ToArray();
    }
}
