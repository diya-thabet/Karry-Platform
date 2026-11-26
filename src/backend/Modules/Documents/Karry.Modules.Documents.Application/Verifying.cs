using System.Security.Cryptography;
using System.Text;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Documents.Domain;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Documents.Application;

/// <summary>
/// The company and token hash of an envelope, readable without a company (ADR-0040 §4): the public page knows only the
/// envelope ID and the token. Written with the envelope, in the same transaction.
/// </summary>
public interface IVerificationKeys
{
    Task<VerificationKey?> FindAsync(Guid envelopeId, CancellationToken cancellationToken);
}

/// <summary>The current company's envelope by ID (RLS).</summary>
public interface IEnvelopeReader
{
    Task<DocumentEnvelope?> FindAsync(Guid envelopeId, CancellationToken cancellationToken);
}

/// <summary>The server's signature of a verified document, once the Workers signed it.</summary>
public sealed record DocumentSignature(string KeyId, string Value, DateTimeOffset SignedAt);

/// <summary>
/// What the public page shows of a genuine document (ADR-0040 §4, US-P2-SIG-06): what and whose it is, its hashes and
/// its signature, no person's name. <c>PdfMatches</c> answers a PDF hash the visitor sent (TC-P2-DOC-002), null if none.
/// </summary>
public sealed record VerifiedDocument(
    Guid EnvelopeId,
    string Type,
    string Company,
    string SiteCode,
    string SiteName,
    DateOnly ShiftDate,
    string ShiftCode,
    DateTimeOffset IssuedAt,
    string ContentHash,
    string PdfHash,
    DocumentSignature? Signature,
    bool? PdfMatches);

/// <summary>
/// Public verification (ADR-0040 §4, TC-P2-SIG-005): an envelope ID and its token give the document; every miss (an ID
/// that is not one, an unknown envelope, a wrong token) gets the same "not found" after the same work: one read of the
/// key and the token's hash compared in constant time, against a stand-in when the envelope does not exist. Only a
/// match reads the company's data, in a scope of its own set to that company.
/// </summary>
public sealed class DocumentVerification(IVerificationKeys keys, IServiceScopeFactory scopes)
{
    public const int MaxTokenLength = 64;

    public static readonly Error NotFound = Error.NotFound("documents.not_found", "No document matches this address. Check the link or the QR code.");

    // Compared when the envelope does not exist, so a miss costs what a wrong token costs.
    private static readonly byte[] StandIn = SHA256.HashData("karry:no-envelope"u8);

    public async Task<Result<VerifiedDocument>> VerifyAsync(string? envelopeId, string? token, string? pdfHash, CancellationToken cancellationToken)
    {
        var id = Guid.TryParse(envelopeId, out var parsed) ? parsed : Guid.Empty;
        var key = id == Guid.Empty ? null : await keys.FindAsync(id, cancellationToken).ConfigureAwait(false);
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(token is { Length: <= MaxTokenLength } ? token : string.Empty));
        var expected = key is null ? StandIn : Convert.FromHexString(key.TokenHash);
        var match = CryptographicOperations.FixedTimeEquals(given, expected);
        if (!match || key is null || token is not { Length: > 0 and <= MaxTokenLength })
        {
            return NotFound;
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(key.TenantId));
        var envelope = await scope.ServiceProvider.GetRequiredService<IEnvelopeReader>().FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (envelope is null)
        {
            return NotFound;
        }

        var company = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync(envelope.TenantId, cancellationToken).ConfigureAwait(false);
        var site = await scope.ServiceProvider.GetRequiredService<ISiteDirectory>().FindAsync(envelope.SiteId, cancellationToken).ConfigureAwait(false);
        var signature = envelope is { Signature: { } value, SignatureKeyId: { } keyId, SignedAt: { } signedAt }
            ? new DocumentSignature(keyId, value, signedAt)
            : null;
        bool? pdfMatches = string.IsNullOrWhiteSpace(pdfHash)
            ? null
            : string.Equals(Normalized(pdfHash), envelope.PdfHash, StringComparison.Ordinal);
        return new VerifiedDocument(
            envelope.Id.Value, envelope.Type, company?.Name ?? string.Empty, site?.Code ?? string.Empty, site?.Name ?? string.Empty,
            envelope.ShiftDate, envelope.ShiftCode, envelope.IssuedAt, envelope.ContentHash, envelope.PdfHash, signature, pdfMatches);
    }

    /// <summary>A PDF hash as the envelope keeps it: <c>sha256:</c> and lower-case hex; a bare hex digest is accepted.</summary>
    private static string Normalized(string hash)
    {
        var trimmed = hash.Trim().ToLowerInvariant();
        return trimmed.StartsWith("sha256:", StringComparison.Ordinal) ? trimmed : "sha256:" + trimmed;
    }
}
