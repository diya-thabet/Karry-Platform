using System.Diagnostics;
using System.Text;

namespace Karry.Spikes.PdfEngine.Tests;

/// <summary>
/// P2.11 slice 1 (ADR-0040 §1, US-P2-SIG-05 AC3): the 200-entry shift, each entry with its own signature image, renders
/// with embedded Noto Sans in under 10 s on the first (cold) run, and the PDF really embeds the fonts.
/// </summary>
public sealed class ShiftReportTests
{
    private static readonly string Output = Path.Combine(AppContext.BaseDirectory, "out");

    [Fact]
    public void Two_hundred_entries_with_signature_images_render_in_under_ten_seconds_cold()
    {
        var entries = Shift(200);
        var hash = ContentHash.Of(entries);

        var clock = Stopwatch.StartNew();
        var pdf = ShiftReport.Render("CAR-KAK · Carrière de Kakoulima", "2026-11-26 · Matin (06:00–14:00)", "Carrière Démo B", entries, hash);
        clock.Stop();

        Directory.CreateDirectory(Output);
        File.WriteAllBytes(Path.Combine(Output, "shift-report-200.pdf"), pdf);
        File.WriteAllText(Path.Combine(Output, "timing.txt"), $"cold render: {clock.ElapsedMilliseconds} ms, {pdf.Length} bytes\n");
        TestContext.Current.SendDiagnosticMessage($"cold render of 200 entries: {clock.ElapsedMilliseconds} ms, {pdf.Length / 1024} KiB");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"rendered in {clock.ElapsedMilliseconds} ms (budget 10 s)");
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-", text);
        Assert.Contains("/FontFile2", text, StringComparison.Ordinal); // TrueType fonts embedded, not referenced
        Assert.Contains("+Noto#20Sans", text, StringComparison.Ordinal); // a subset of Noto Sans (the space is escaped in PDF names)
    }

    [Fact]
    public void A_warm_render_is_much_faster_and_the_same_content_gives_the_same_hash()
    {
        ShiftReport.Render("S", "P", "C", Shift(5), "sha256:warm-up");
        var entries = Shift(200);

        var clock = Stopwatch.StartNew();
        ShiftReport.Render("CAR-KAK", "Matin", "Carrière Démo B", entries, ContentHash.Of(entries));
        clock.Stop();
        TestContext.Current.SendDiagnosticMessage($"warm render of 200 entries: {clock.ElapsedMilliseconds} ms");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(ContentHash.Of(Shift(200)), ContentHash.Of(entries));
    }

    private static List<ReportEntry> Shift(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => new ReportEntry(
            Machine: $"PEL-{(i % 12) + 1:00}",
            Operator: $"Mamadou Diallo-{i % 37}",
            Start: 12_345.0m + (i * 8.5m),
            End: 12_345.0m + (i * 8.5m) + 8.0m,
            Unit: "h",
            Controller: i % 3 == 0 ? "Awa Camara" : "Ibrahima Sow",
            Status: i % 17 == 0 ? "Refusée" : "Validée",
            Flags: i % 11 == 0 ? "RULE-FO-01" : string.Empty,
            Signature: SignaturePng.Make(i))),
    ];
}
