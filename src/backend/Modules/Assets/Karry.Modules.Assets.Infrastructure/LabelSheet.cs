using Karry.Modules.Assets.Application;
using Karry.Modules.Assets.Domain;
using QRCoder;

namespace Karry.Modules.Assets.Infrastructure;

/// <summary>
/// A4 sheets of 3 × 8 labels of 70 × 37 mm (the common self-adhesive sheet), each with the QR code of the asset's label
/// and its code, name and plate or serial number, so a person can tell the machine without a phone (ADR-0026).
/// </summary>
internal sealed class LabelSheet : ILabelSheet
{
    public const int Columns = 3;
    public const int Rows = 8;
    private const double Width = 70;
    private const double Height = 37;
    private const double TopMargin = 0.5;
    private const double Padding = 3;
    private const double QrSize = Height - (2 * Padding);

    /// <summary>Characters of the name that fit the text column at 8 pt.</summary>
    private const int NameLength = 22;

    public byte[] Render(IReadOnlyList<Asset> assets, int skip)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var pdf = new PdfWriter();
        var slots = assets.Select((asset, i) => (asset, slot: skip + i)).GroupBy(a => a.slot / ILabelSheet.PerPage);
        foreach (var page in slots)
        {
            pdf.AddPage(draw =>
            {
                foreach (var (asset, slot) in page)
                {
                    var index = slot % ILabelSheet.PerPage;
                    Draw(draw, asset, Width * (index % Columns), TopMargin + (Height * (index / Columns)));
                }
            });
        }

        return pdf.ToArray();
    }

    /// <summary>The QR matrix of a label, quiet zone included (4 modules), error correction M (15 %: survives scratches).</summary>
    public static IReadOnlyList<bool[]> Matrix(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return [.. data.ModuleMatrix.Select(row => Enumerable.Range(0, row.Length).Select(i => row[i]).ToArray())];
    }

    /// <summary>A label whose top left corner is (<paramref name="left"/>, <paramref name="top"/>) mm from the page's top left.</summary>
    private static void Draw(PdfPage page, Asset asset, double left, double top)
    {
        var matrix = Matrix(AssetLabels.Text(asset.LabelToken));
        var module = PdfWriter.Mm(QrSize) / matrix.Count;
        var x0 = PdfWriter.Mm(left + Padding);
        var y0 = PdfWriter.PageHeight - PdfWriter.Mm(top + Padding);
        for (var row = 0; row < matrix.Count; row++)
        {
            // One rectangle per run of dark modules: a fifth of the operations of one per module.
            for (var col = 0; col < matrix[row].Length; col++)
            {
                if (!matrix[row][col])
                {
                    continue;
                }

                var start = col;
                while (col + 1 < matrix[row].Length && matrix[row][col + 1])
                {
                    col++;
                }

                page.Fill(x0 + (start * module), y0 - ((row + 1) * module), (col - start + 1) * module, module);
            }
        }

        var textX = PdfWriter.Mm(left + Padding + QrSize + 1);
        var textTop = PdfWriter.PageHeight - PdfWriter.Mm(top + Padding);
        page.Text(textX, textTop - 14, 13, asset.Code, bold: true);
        page.Text(textX, textTop - 28, 8, asset.Name.Length > NameLength ? $"{asset.Name[..(NameLength - 3)]}..." : asset.Name);
        if ((asset.Plate ?? asset.SerialNumber) is { } identifier)
        {
            page.Text(textX, textTop - 40, 8, identifier);
        }
    }
}
