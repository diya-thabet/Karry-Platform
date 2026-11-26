using System.Security.Cryptography;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Documents.Domain;

public readonly record struct DocumentEnvelopeId(Guid Value) : IEntityId<DocumentEnvelopeId>
{
    public static DocumentEnvelopeId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>A shift report was issued (ADR-0040 §3): its hashes are in the ledger from then on, with who issued it.</summary>
[EventType("documents.shift_report_issued.v1")]
[AuditSubject("document", nameof(EnvelopeId), Site = nameof(SiteId))]
public sealed record ShiftReportIssued(
    Guid EnvelopeId, Guid SiteId, DateOnly ShiftDate, string ShiftCode, string ContentHash, string PdfHash, string? IssuedBy) : DomainEvent;

/// <summary>
/// One issued document (ADR-0040 §3): what it is about, the hash of its content (printed in its footer) and of its PDF,
/// the hash of its verification token (the token itself is never stored), who issued it and when. Never changed, except
/// the server's signature, added once when the signing service answers. Issuing again makes a new envelope.
/// </summary>
public sealed class DocumentEnvelope : AggregateRoot<DocumentEnvelopeId>, ITenantOwned
{
    public const string ShiftReport = "shift_report";

    private DocumentEnvelope(DocumentEnvelopeId id)
        : base(id)
    {
    }

    // EF Core
    private DocumentEnvelope()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public string Type { get; private set; } = null!;

    public Guid SiteId { get; private set; }

    public DateOnly ShiftDate { get; private set; }

    public string ShiftCode { get; private set; } = null!;

    public string ContentHash { get; private set; } = null!;

    public string PdfHash { get; private set; } = null!;

    /// <summary>SHA-256 (hex) of the verification token: the token travels only in the QR's fragment.</summary>
    public string TokenHash { get; private set; } = null!;

    public string? IssuedBy { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    /// <summary>The server's signature over the PDF hash (ADR-0033), once the signing service answered; null until then.</summary>
    public string? Signature { get; private set; }

    public string? SignatureKeyId { get; private set; }

    public DateTimeOffset? SignedAt { get; private set; }

    /// <summary>A new verification token: 128 random bits, base64url (it goes in the QR only).</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The hash of a token as it is stored and compared (lower-case hex).</summary>
    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    public static DocumentEnvelope IssueShiftReport(
        TimeProvider clock, DocumentEnvelopeId id, TenantId tenant, Guid siteId, DateOnly shiftDate, string shiftCode, string contentHash, string pdfHash,
        string token, string? issuedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var envelope = new DocumentEnvelope(id)
        {
            TenantId = tenant,
            Type = ShiftReport,
            SiteId = siteId,
            ShiftDate = shiftDate,
            ShiftCode = shiftCode,
            ContentHash = contentHash,
            PdfHash = pdfHash,
            TokenHash = HashOf(token),
            IssuedBy = issuedBy,
            IssuedAt = clock.GetUtcNow(),
        };
        envelope.Raise(new ShiftReportIssued(id.Value, siteId, shiftDate, shiftCode, contentHash, pdfHash, issuedBy) { OccurredAt = envelope.IssuedAt });
        return envelope;
    }
}
