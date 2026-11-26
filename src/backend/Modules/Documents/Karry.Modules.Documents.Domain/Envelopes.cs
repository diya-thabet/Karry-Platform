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

/// <summary>The server signed an issued document's envelope (ADR-0040 §3): ledgered with the key version.</summary>
[EventType("documents.envelope_signed.v1")]
[AuditSubject("document", nameof(EnvelopeId), Site = nameof(SiteId))]
public sealed record EnvelopeSigned(Guid EnvelopeId, Guid SiteId, string KeyId, string Signature) : DomainEvent;

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

    /// <summary>
    /// What the server signs: the SHA-256 of <c>{envelopeId}|{contentHash}|{pdfHash}</c>, which binds both hashes to this
    /// envelope (a signature cannot be moved to another document).
    /// </summary>
    public byte[] SignedDigest() => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{Id.Value:D}|{ContentHash}|{PdfHash}"));

    /// <summary>Adds the server's signature once; a signed envelope keeps its first signature (returns false).</summary>
    public bool Sign(TimeProvider clock, string keyId, string signature)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Signature is not null)
        {
            return false;
        }

        Signature = signature;
        SignatureKeyId = keyId;
        SignedAt = clock.GetUtcNow();
        Raise(new EnvelopeSigned(Id.Value, SiteId, keyId, signature) { OccurredAt = SignedAt.Value });
        return true;
    }

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

/// <summary>
/// An envelope's company and token hash, kept outside the company's rows (ADR-0040 §4): the public page knows only the
/// envelope ID and the token, so this is read before any company is known. Written with the envelope, never changed.
/// </summary>
public sealed class VerificationKey
{
    public VerificationKey(Guid envelopeId, Guid tenantId, string tokenHash)
    {
        EnvelopeId = envelopeId;
        TenantId = tenantId;
        TokenHash = tokenHash;
    }

    public Guid EnvelopeId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>SHA-256 (hex) of the token, as in the envelope.</summary>
    public string TokenHash { get; private set; }

    public static VerificationKey Of(DocumentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new VerificationKey(envelope.Id.Value, envelope.TenantId.Value, envelope.TokenHash);
    }
}
