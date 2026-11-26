using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Karry.Spikes.PdfEngine.Tests;

/// <summary>Noto Sans from the Documents module's fonts, embedded in every PDF; nothing from the machine's own fonts.</summary>
public sealed class NotoFonts : IFontResolver
{
    public const string Family = "Noto Sans";

    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "fonts");

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? "NotoSans-Bold" : "NotoSans-Regular");

    public byte[]? GetFont(string faceName) => File.ReadAllBytes(Path.Combine(Directory, $"{faceName}.ttf"));
}

/// <summary>One entry of a shift as the report prints it.</summary>
public sealed record ReportEntry(string Machine, string Operator, decimal Start, decimal End, string Unit, string Controller, string Status, string Flags, byte[] Signature);

/// <summary>The shift report's layout as the spike measures it: a header, a table of entries with signature images, a footer.</summary>
public static class ShiftReport
{
    private static readonly Lock Gate = new();

    /// <summary>Registers the fonts once per process (PDFsharp refuses a second resolver).</summary>
    public static void UseNotoFonts()
    {
        lock (Gate)
        {
            if (GlobalFontSettings.FontResolver is not NotoFonts)
            {
                GlobalFontSettings.FontResolver = new NotoFonts();
            }
        }
    }

    public static byte[] Render(string site, string shift, string company, IReadOnlyList<ReportEntry> entries, string contentHash)
    {
        UseNotoFonts();
        var document = new Document();
        document.Styles[StyleNames.Normal]!.Font.Name = NotoFonts.Family;
        document.Styles[StyleNames.Normal]!.Font.Size = 8;
        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.5);

        var title = section.AddParagraph($"Rapport de poste · {site} · {shift}");
        title.Format.Font.Size = 14;
        title.Format.Font.Bold = true;
        section.AddParagraph(company).Format.SpaceAfter = Unit.FromPoint(8);

        var table = section.AddTable();
        table.Borders.Width = 0.25;
        foreach (var width in new[] { 2.2, 3.6, 2.4, 2.4, 1.6, 3.2, 2.0, 3.0, 3.6 })
        {
            table.AddColumn(Unit.FromCentimeter(width));
        }

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Format.Font.Bold = true;
        string[] titles = ["Engin", "Opérateur", "Début", "Fin", "Δ", "Contrôleur", "Statut", "Alertes", "Signature"];
        for (var i = 0; i < titles.Length; i++)
        {
            head.Cells[i].AddParagraph(titles[i]);
        }

        foreach (var entry in entries)
        {
            var row = table.AddRow();
            row.VerticalAlignment = VerticalAlignment.Center;
            row.Cells[0].AddParagraph(entry.Machine);
            row.Cells[1].AddParagraph(entry.Operator);
            row.Cells[2].AddParagraph(entry.Start.ToString("N1", CultureInfo.GetCultureInfo("fr-FR")));
            row.Cells[3].AddParagraph(entry.End.ToString("N1", CultureInfo.GetCultureInfo("fr-FR")));
            row.Cells[4].AddParagraph($"{(entry.End - entry.Start).ToString("N1", CultureInfo.GetCultureInfo("fr-FR"))} {entry.Unit}");
            row.Cells[5].AddParagraph(entry.Controller);
            row.Cells[6].AddParagraph(entry.Status);
            row.Cells[7].AddParagraph(entry.Flags);
            var image = row.Cells[8].AddParagraph().AddImage("base64:" + Convert.ToBase64String(entry.Signature));
            image.Width = Unit.FromCentimeter(3.0);
            image.LockAspectRatio = true;
        }

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.AddText($"Empreinte du contenu : {contentHash} · page ");
        footer.AddPageField();
        footer.AddText(" / ");
        footer.AddNumPagesField();

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, closeStream: false);
        return output.ToArray();
    }
}

/// <summary>A small grey-scale PNG of a handwritten-looking stroke, different for each seed (no imaging library needed).</summary>
public static class SignaturePng
{
    public static byte[] Make(int seed, int width = 240, int height = 80)
    {
        var pixels = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            pixels[y * (width + 1)] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                pixels[(y * (width + 1)) + 1 + x] = 255;
            }
        }

        var random = new Random(seed);
        double px = 10, py = height / 2.0;
        for (var step = 0; step < 900; step++)
        {
            px = Math.Clamp(px + 0.25, 1, width - 2);
            py = Math.Clamp(py + ((random.NextDouble() - 0.5) * 3) + (Math.Sin((step + seed) / 20.0) * 0.6), 2, height - 3);
            for (var dy = -1; dy <= 1; dy++)
            {
                pixels[(((int)py + dy) * (width + 1)) + 1 + (int)px] = 20;
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 0; // grey
        Chunk(png, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(pixels);
            }

            Chunk(png, "IDAT", compressed.ToArray());
        }

        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        png.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

/// <summary>The content hash printed in the footer: SHA-256 of the report's canonical content (here, its entries).</summary>
public static class ContentHash
{
    public static string Of(IEnumerable<ReportEntry> entries) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', entries.Select(e => $"{e.Machine}|{e.Operator}|{e.Start}|{e.End}|{e.Status}")))));
}
