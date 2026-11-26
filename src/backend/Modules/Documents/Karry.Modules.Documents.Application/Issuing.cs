using System.Security.Cryptography;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Storage;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Documents.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Documents.Application;

/// <summary>Documents' settings (section <c>Documents</c>).</summary>
public sealed class DocumentsOptions
{
    public const string Section = "Documents";

    /// <summary>
    /// Where the public verification page is (ADR-0040 §4): the platform address until the <c>verify.</c> domain
    /// exists. A QR opens <c>{VerifyOrigin}/verify/{envelopeId}#{token}</c>.
    /// </summary>
    public string VerifyOrigin { get; set; } = "http://localhost:5173";

    public Uri VerificationFor(Guid envelopeId, string token) => new($"{VerifyOrigin.TrimEnd('/')}/verify/{envelopeId:D}#{token}");
}

/// <summary>The current company's document envelopes (RLS); saving one ledgers its event in the same transaction.</summary>
public interface IDocumentEnvelopeStore
{
    void Add(DocumentEnvelope envelope);

    /// <summary>Envelopes still without the server's signature, oldest first.</summary>
    Task<IReadOnlyList<DocumentEnvelope>> UnsignedAsync(int take, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Draws a shift report as a PDF (Documents.Infrastructure: PDFsharp + MigraDoc).</summary>
public interface IShiftReportRenderer
{
    byte[] Render(ShiftReportContent content, Uri verification, IReadOnlyDictionary<string, byte[]> images, string language);
}

/// <summary>An issued document: its envelope, the PDF, and the hashes the envelope keeps.</summary>
public sealed record IssuedDocument(Guid EnvelopeId, byte[] Pdf, string ContentHash, string PdfHash);

/// <summary>
/// Issues a shift report (ADR-0040 §3, US-P2-SIG-05): the reader's content, its signature images from the evidence
/// store, a new envelope with a fresh token, the PDF with the content hash and the QR, then the envelope saved with its
/// ledger entry (the content and PDF hashes). The server's signature is added later by the Workers (slice 3c).
/// </summary>
public sealed class ShiftReportIssuing(
    ShiftReportContents contents,
    IDocumentEnvelopeStore store,
    IShiftReportRenderer renderer,
    IEvidenceStore evidence,
    ITenantContext tenant,
    ICallerAccess caller,
    IOptions<DocumentsOptions> options,
    TimeProvider clock)
{
    public async Task<Result<IssuedDocument>> IssueAsync(Guid siteId, DateOnly date, string? shift, string? language, CancellationToken cancellationToken)
    {
        var report = await contents.OfShiftAsync(siteId, date, shift, cancellationToken).ConfigureAwait(false);
        if (!report.IsSuccess)
        {
            return report.Error;
        }

        var company = tenant.Current ?? throw new InvalidOperationException("A document belongs to a company.");
        var images = await ImagesAsync(report.Value.ImageKeys, cancellationToken).ConfigureAwait(false);
        var id = EntityId.New<DocumentEnvelopeId>(clock);
        var token = DocumentEnvelope.NewToken();
        var content = report.Value.Content;
        var pdf = renderer.Render(content, options.Value.VerificationFor(id.Value, token), images, language ?? "fr");
        var pdfHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(pdf));
        store.Add(DocumentEnvelope.IssueShiftReport(clock, id, company, siteId, date, shift!, content.Hash(), pdfHash, token, caller.Subject));
        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return new IssuedDocument(id.Value, pdf, content.Hash(), pdfHash);
    }

    /// <summary>The signature images the store has; one it cannot give leaves its cell empty, never the report undone.</summary>
    private async Task<IReadOnlyDictionary<string, byte[]>> ImagesAsync(IReadOnlyDictionary<string, string> keys, CancellationToken cancellationToken)
    {
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!evidence.Available)
        {
            return images;
        }

        foreach (var (hash, key) in keys)
        {
            if (await evidence.GetAsync(key, cancellationToken).ConfigureAwait(false) is { } image)
            {
                images[hash] = image.Content;
            }
        }

        return images;
    }
}
