using System.Globalization;
using Karry.Modules.Documents.Domain;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using QRCoder;

namespace Karry.Modules.Documents.Infrastructure;

/// <summary>
/// Noto Sans, embedded in the assembly from <c>Documents/Fonts</c> (SIL OFL 1.1), is every PDF's font (ADR-0040 §1):
/// a report looks the same wherever it is made, and no machine font ends in a document.
/// </summary>
internal sealed class NotoFonts : IFontResolver
{
    public const string Family = "Noto Sans";

    private static readonly Lock Gate = new();

    /// <summary>Registers the fonts once per process: PDFsharp takes one resolver, and refuses a second.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (GlobalFontSettings.FontResolver is not NotoFonts)
            {
                GlobalFontSettings.FontResolver = new NotoFonts();
            }
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) => new(isBold ? "NotoSans-Bold" : "NotoSans-Regular");

    public byte[]? GetFont(string faceName)
    {
        using var stream = typeof(NotoFonts).Assembly.GetManifestResourceStream($"Karry.Documents.Fonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"The font {faceName} is not embedded in the Documents module.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}

/// <summary>
/// The shift report as a PDF (ADR-0040 §1, §3, US-P2-SIG-05): A4 landscape, a header naming the company, site and shift,
/// the reader's table (absent columns stay absent), the signature images, and a footer with the content hash, the page
/// and a QR code to the public verification page. Column titles in French or English; Arabic and N'Ko need their own
/// fonts and right-to-left layout, and fall back to French for now.
/// </summary>
public static class ShiftReportPdf
{
    private static readonly Dictionary<string, (string Fr, string En)> Titles = new(StringComparer.Ordinal)
    {
        ["machine"] = ("Engin", "Machine"),
        ["operator"] = ("Opérateur", "Operator"),
        ["start"] = ("Début", "Start"),
        ["end"] = ("Fin", "End"),
        ["delta"] = ("Δ", "Δ"),
        ["unit"] = ("Unité", "Unit"),
        ["status"] = ("Saisie", "Entry"),
        ["review"] = ("Contrôle", "Review"),
        ["controller"] = ("Contrôleur", "Controller"),
        ["flags"] = ("Alertes", "Flags"),
        ["signature"] = ("Signature", "Signature"),
    };

    /// <summary>The PDF's bytes.</summary>
    public static byte[] Render(ShiftReportContent content, Uri verification, IReadOnlyDictionary<string, byte[]> images, string language)
    {
        var renderer = new PdfDocumentRenderer { Document = Build(content, verification, images, language) };
        renderer.RenderDocument();
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, closeStream: false);
        return output.ToArray();
    }

    /// <summary>The document before rendering: what it says can be checked without reading a PDF.</summary>
    public static Document Build(ShiftReportContent content, Uri verification, IReadOnlyDictionary<string, byte[]> images, string language)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(images);
        NotoFonts.Register();
        var english = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var document = new Document();
        document.Info.Title = english ? "Shift report" : "Rapport de poste";
        document.Styles[StyleNames.Normal]!.Font.Name = NotoFonts.Family;
        document.Styles[StyleNames.Normal]!.Font.Size = 8;
        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.5);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(3.2);

        var date = content.ShiftDate.ToString(english ? "yyyy-MM-dd" : "dd/MM/yyyy", CultureInfo.InvariantCulture);
        var title = section.AddParagraph($"{(english ? "Shift report" : "Rapport de poste")} · {content.SiteCode} · {date} · {content.ShiftCode}");
        title.Format.Font.Size = 14;
        title.Format.Font.Bold = true;
        section.AddParagraph($"{content.Company} · {content.SiteName}").Format.SpaceAfter = Unit.FromPoint(8);

        if (content.Rows.Count == 0)
        {
            section.AddParagraph(english ? "No entry in this shift." : "Aucune saisie pour ce poste.");
        }
        else
        {
            Table(section, content, images, english);
        }

        Footer(section, content.Hash(), verification, english);
        return document;
    }

    private static void Table(Section section, ShiftReportContent content, IReadOnlyDictionary<string, byte[]> images, bool english)
    {
        var table = section.AddTable();
        table.Borders.Width = 0.25;
        var signature = -1;
        for (var i = 0; i < content.Columns.Count; i++)
        {
            var code = content.Columns[i];
            table.AddColumn(Unit.FromCentimeter(code switch { "signature" => 3.4, "operator" or "controller" => 3.4, "flags" => 3.0, "unit" or "delta" => 1.6, _ => 2.2 }));
            signature = code == "signature" ? i : signature;
        }

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Format.Font.Bold = true;
        for (var i = 0; i < content.Columns.Count; i++)
        {
            var code = content.Columns[i];
            head.Cells[i].AddParagraph(Titles.TryGetValue(code, out var t) ? (english ? t.En : t.Fr) : code);
        }

        foreach (var values in content.Rows)
        {
            var row = table.AddRow();
            row.VerticalAlignment = VerticalAlignment.Center;
            for (var i = 0; i < values.Count; i++)
            {
                if (i == signature && images.TryGetValue(values[i], out var png))
                {
                    var image = row.Cells[i].AddParagraph().AddImage("base64:" + Convert.ToBase64String(png));
                    image.Width = Unit.FromCentimeter(3.0);
                    image.LockAspectRatio = true;
                }
                else if (i != signature)
                {
                    row.Cells[i].AddParagraph(values[i]);
                }
            }
        }
    }

    private static void Footer(Section section, string hash, Uri verification, bool english)
    {
        var footer = section.Footers.Primary;
        var table = footer.AddTable();
        table.AddColumn(Unit.FromCentimeter(22.0));
        table.AddColumn(Unit.FromCentimeter(4.0));
        var row = table.AddRow();
        var text = row.Cells[0].AddParagraph();
        text.Format.Font.Size = 7;
        text.AddText($"{(english ? "Content hash" : "Empreinte du contenu")} : {hash}");
        text.AddLineBreak();
        text.AddText($"{(english ? "Check this document" : "Vérifier ce document")} : {verification.GetLeftPart(UriPartial.Path)}");
        text.AddLineBreak();
        text.AddText(english ? "Page " : "Page ");
        text.AddPageField();
        text.AddText(" / ");
        text.AddNumPagesField();
        var qr = row.Cells[1].AddParagraph().AddImage("base64:" + Convert.ToBase64String(Qr(verification)));
        qr.Width = Unit.FromCentimeter(2.4);
        qr.LockAspectRatio = true;
    }

    /// <summary>
    /// The QR of the verification address, fragment (the token) included: a phone opens it as is. Drawn here as an 8-bit
    /// grey PNG from QRCoder's module matrix: PDFsharp's core build cannot read QRCoder's own 1-bit PNG, and MigraDoc
    /// would then print an error text in its place without failing.
    /// </summary>
    public static byte[] Qr(Uri verification, int scale = 8)
    {
        ArgumentNullException.ThrowIfNull(verification);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(verification.ToString(), QRCodeGenerator.ECCLevel.M);
        var modules = data.ModuleMatrix; // includes QRCoder's quiet zone of four modules
        var size = modules.Count * scale;
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[(y * size) + x] = modules[y / scale][x / scale] ? (byte)0 : (byte)255;
            }
        }

        return GreyPng.Encode(size, size, pixels);
    }
}

/// <summary>A minimal 8-bit grey PNG writer (a format PDFsharp's core build reads).</summary>
internal static class GreyPng
{
    public static byte[] Encode(int width, int height, byte[] pixels)
    {
        var raw = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, y * width, raw, (y * (width + 1)) + 1, width); // filter byte 0: none
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 0; // grey
        Chunk(png, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(png, "IDAT", compressed.ToArray());
        }

        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        png.Write(typed);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
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
