# Spike P2.11 slice 1 — PDF engine for the shift report

**Question.** Can **PDFsharp + MigraDoc 6.2.4** (MIT, the last release before 6.5.0 became AGPL-3.0-only) render the
200-entry shift report, each entry with its own signature image, with embedded fonts, in under 10 s
(US-P2-SIG-05 AC3)? Decision record: [ADR-0040 §1](../../docs/adr/0040-documents-and-public-verification.md#1-a-pdf-engine-with-a-permissive-licence-chosen-by-a-spike).

**Status:** ✅ Answered (2026-11-26, .NET 10.0.402, Windows 11 developer machine; CI re-runs it on Linux).

| Check | Result |
|---|---|
| Licence, read from the packages' `.nuspec` | `PDFsharp-MigraDoc` 6.2.4 and `PDFsharp` 6.2.4: **MIT**; 6.5.0: AGPL-3.0-only. Published 2026-01-06 |
| 200 entries, 200 distinct signature PNGs (240×80), A4 landscape, header and footer with the content hash | **606–757 ms cold** (first render in the process, fonts loaded), 15 pages, 302 KB |
| Fonts | Noto Sans Regular and Bold (SIL OFL 1.1, [the Documents module's fonts](../../src/backend/Modules/Documents/Fonts/README.md)) embedded as TrueType subsets (`/FontFile2`), resolved by an `IFontResolver`; no system font used |
| Transitive dependency | `System.Security.Cryptography.Pkcs` 8.0.1 has GHSA-2fmv-rv2c-59x7 and GHSA-846f-f764-76mx: pinned to 10.0.13, as the product already pins it |

**Decisions for slices 2–3**
- Use PDFsharp + MigraDoc 6.2.4; QuestPDF is not needed (no owner licence decision).
- Register the font resolver once per process (PDFsharp refuses a second one) and read the fonts from the module's
  `Fonts` folder; Arabic and N'Ko names need their Noto fonts when reports are issued in those scripts.
- Pin `System.Security.Cryptography.Pkcs` centrally (already done for MailKit).
- The QR code needs a library (slice 3 chooses one, MIT, with the same licence check).
- A PDF cannot hold its own hash: the footer prints the content hash, the envelope signs both (ADR-0040 §3).

## Run

```bash
cd spikes
dotnet test --project pdf-engine/tests/Karry.Spikes.PdfEngine.Tests -c Release
# the PDF and the timing are written to bin/Release/net10.0/out/
```
