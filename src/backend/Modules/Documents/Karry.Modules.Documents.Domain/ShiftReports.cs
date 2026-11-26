using System.Security.Cryptography;
using System.Text.Json;
using Karry.SharedKernel.Canonical;

namespace Karry.Modules.Documents.Domain;

/// <summary>
/// What a shift report says (ADR-0040 §3), exactly as its reader sees it: the company, the site, the shift, and a table
/// of strings (column codes, then one row per entry). A column the reader's role may not see is absent, so two readers
/// may get two contents; each issued document is bound to its own. Its hash is printed in the PDF's footer.
/// </summary>
public sealed record ShiftReportContent(
    string Company, string SiteCode, string SiteName, DateOnly ShiftDate, string ShiftCode,
    IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public const string Type = "shift_report";

    /// <summary>The content in its canonical form (RFC 8785): the same content always gives the same bytes.</summary>
    public byte[] Canonical() => JsonCanonicalizer.Canonicalize(JsonSerializer.Serialize(new
    {
        type = Type,
        company = Company,
        site = new { code = SiteCode, name = SiteName },
        shift = new { date = ShiftDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), code = ShiftCode },
        columns = Columns,
        rows = Rows,
    }));

    /// <summary>The content hash, <c>sha256:</c> and 64 lower-case hex digits, as the footer and the envelope carry it.</summary>
    public string Hash() => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Canonical()));
}
