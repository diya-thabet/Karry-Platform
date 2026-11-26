using System.Diagnostics;
using System.Text;
using Karry.Modules.Documents.Domain;
using Karry.Modules.Documents.Infrastructure;
using MigraDoc.DocumentObjectModel.IO;

namespace Karry.Modules.Documents.Tests;

/// <summary>
/// The shift report as a PDF (ADR-0040 §1, §3, US-P2-SIG-05): 200 entries with their signature images in under 10 s
/// with the fonts embedded (TC-P2-DOC-001, timing); the footer carries the content hash and a QR to the verification
/// page; the reader's table is printed as it is (an absent column stays absent).
/// </summary>
[Trait("Story", "US-P2-SIG-05")]
public sealed class ShiftReportPdfTests
{
    private static readonly Uri Verification = new("https://karry.test/verify/0192f0a1-0000-7000-8000-0000000000d1#token-abc");
    private static readonly string Signature = "sha256:" + new string('5', 64);

    [Fact]
    [Trait("TestCase", "TC-P2-DOC-001")]
    public void Two_hundred_entries_with_their_signatures_render_in_under_ten_seconds_with_the_fonts_embedded()
    {
        var content = Content(200);
        var images = new Dictionary<string, byte[]> { [Signature] = ShiftReportPdf.Qr(new Uri("https://karry.test/stroke")) };

        var clock = Stopwatch.StartNew();
        var pdf = ShiftReportPdf.Render(content, Verification, images, "fr");
        clock.Stop();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        var text = Encoding.Latin1.GetString(pdf);
        text.Should().StartWith("%PDF-").And.Contain("/FontFile2").And.Contain("+Noto#20Sans");
        System.Text.RegularExpressions.Regex.Count(text, @"/Subtype\s*/Image").Should().BeGreaterThanOrEqualTo(2, "the signature image and the QR are images");
    }

    [Fact]
    public void The_footer_carries_the_content_hash_and_the_verification_address_without_its_token_and_a_qr_code()
    {
        var content = Content(2);

        var ddl = DdlWriter.WriteToString(ShiftReportPdf.Build(content, Verification, new Dictionary<string, byte[]>(), "fr"));

        ddl.Should().Contain($"Empreinte du contenu : {content.Hash()}").And.Contain("karry.test/verify/0192f0a1-0000-7000-8000-0000000000d1")
            .And.NotContain("token-abc", "the token travels in the QR only, never in printed text")
            .And.Contain("Rapport de poste · CAR-KAK · 26/11/2026 · M").And.Contain("\\image");
    }

    [Fact]
    public void Column_titles_follow_the_readers_language_and_a_column_absent_from_the_content_is_absent_from_the_pdf()
    {
        var masked = Content(1) with { Columns = ["machine", "start", "end"], Rows = [["PEL-01", "1.0", "2.0"]] };

        var english = DdlWriter.WriteToString(ShiftReportPdf.Build(masked, Verification, new Dictionary<string, byte[]>(), "en"));
        var french = DdlWriter.WriteToString(ShiftReportPdf.Build(masked, Verification, new Dictionary<string, byte[]>(), "fr"));

        english.Should().Contain("Machine").And.Contain("Shift report").And.NotContain("Operator");
        french.Should().Contain("Engin").And.NotContain("Opérateur");
    }

    [Fact]
    public void A_shift_without_entries_says_so_and_each_address_gets_its_own_qr()
    {
        var ddl = DdlWriter.WriteToString(ShiftReportPdf.Build(Content(0), Verification, new Dictionary<string, byte[]>(), "fr"));

        ddl.Should().Contain("Aucune saisie pour ce poste.");
        var qr = ShiftReportPdf.Qr(Verification);
        qr.Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
        ShiftReportPdf.Qr(new Uri("https://karry.test/verify/other#t")).Should().NotEqual(qr);
    }

    private static ShiftReportContent Content(int count) => new(
        "Carrière Démo B", "CAR-KAK", "Carrière de Kakoulima", new DateOnly(2026, 11, 26), "M",
        ["machine", "operator", "start", "end", "delta", "unit", "status", "review", "controller", "flags", "signature"],
        [.. Enumerable.Range(0, count).Select(i => (IReadOnlyList<string>)[
            $"PEL-{(i % 12) + 1:00}", "Mamadou Diallo", $"{1250 + (i * 8)}.0", $"{1258 + (i * 8)}.0", "8.0", "hours", "closed", "approved", "Awa Camara",
            i % 11 == 0 ? "RULE-FO-01" : "", Signature])]);
}
