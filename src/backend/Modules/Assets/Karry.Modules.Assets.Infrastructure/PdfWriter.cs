using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Karry.Modules.Assets.Infrastructure;

/// <summary>
/// The least PDF the label sheets need (PDF 1.4): pages of filled rectangles and text in the standard Helvetica fonts,
/// which every reader has, so no font file is embedded (ADR-0026). Coordinates are in points from the bottom left.
/// </summary>
internal sealed class PdfWriter
{
    /// <summary>A4 in points.</summary>
    public const double PageWidth = 595.276;
    public const double PageHeight = 841.89;

    private readonly List<string> pages = [];

    public static double Mm(double millimetres) => millimetres * 72 / 25.4;

    /// <summary>Adds a page drawn by <paramref name="draw"/>.</summary>
    public void AddPage(Action<PdfPage> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var page = new PdfPage();
        draw(page);
        pages.Add(page.Content);
    }

    public byte[] ToArray()
    {
        // Objects: 1 catalog, 2 page tree, 3 Helvetica, 4 Helvetica-Bold, then a page and its content per page.
        var objects = new List<byte[]>
        {
            Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
            Latin1($"<< /Type /Pages /Kids [{string.Join(' ', pages.Select((_, i) => $"{5 + (2 * i)} 0 R"))}] /Count {pages.Count} >>"),
            Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
        };
        for (var i = 0; i < pages.Count; i++)
        {
            objects.Add(Latin1(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0.###} {PageHeight:0.###}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + (2 * i)} 0 R >>")));
            objects.Add(Stream(Latin1(pages[i])));
        }

        using var output = new MemoryStream();
        Write(output, Latin1("%PDF-1.4\n%âãÏÓ\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write(output, Latin1($"{i + 1} 0 obj\n"));
            Write(output, objects[i]);
            Write(output, Latin1("\nendobj\n"));
        }

        var xref = output.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(output, Latin1(table.ToString()));
        return output.ToArray();
    }

    /// <summary>Latin-1 bytes: the PDF syntax is ASCII, and WinAnsi text matches Latin-1 for French and other Western letters.</summary>
    internal static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Stream(byte[] content)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(content);
        }

        var body = compressed.ToArray();
        return [.. Latin1($"<< /Length {body.Length} /Filter /FlateDecode >>\nstream\n"), .. body, .. Latin1("\nendstream")];
    }

    private static void Write(Stream output, byte[] bytes) => output.Write(bytes);
}

/// <summary>Drawing operations of one page.</summary>
internal sealed class PdfPage
{
    private readonly StringBuilder content = new();

    public string Content => content.ToString();

    /// <summary>A black rectangle.</summary>
    public void Fill(double x, double y, double width, double height) =>
        content.Append(CultureInfo.InvariantCulture, $"{x:0.###} {y:0.###} {width:0.###} {height:0.###} re f\n");

    /// <summary>One line of text from its baseline at (<paramref name="x"/>, <paramref name="y"/>); <paramref name="bold"/> for Helvetica-Bold.</summary>
    public void Text(double x, double y, double size, string text, bool bold = false) =>
        content.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.###} {y:0.###} Td ({Escape(text)}) Tj ET\n");

    /// <summary>Characters outside Latin-1 become "?"; the string delimiters and backslash are escaped.</summary>
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            escaped.Append(c switch
            {
                '(' or ')' or '\\' => $"\\{c}",
                < ' ' or > 'ÿ' => "?",
                _ => c.ToString(),
            });
        }

        return escaped.ToString();
    }
}
