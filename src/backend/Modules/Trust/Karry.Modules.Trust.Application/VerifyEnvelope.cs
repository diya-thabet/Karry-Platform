using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Devices;
using Karry.Modules.Trust.Contracts;
using Karry.Modules.Trust.Domain;

namespace Karry.Modules.Trust.Application;

/// <summary>An envelope as stored (<c>audit.signature_envelopes</c>).</summary>
public sealed record StoredEnvelope(
    Guid EnvelopeId, string RecordType, Guid RecordId, int RecordVersion, string EnvelopeHash, string Canonical, Guid DeviceId, string DeviceSignature,
    DateTimeOffset ReceivedAt);

/// <summary>Its countersignature, with the published public key of its kid (null when that key is not published).</summary>
public sealed record StoredCountersignature(string KeyId, string Signature, DateTimeOffset SignedAt, string? PublicKeyPem);

/// <summary>Everything stored about an envelope: itself, its countersignature, its ledger entry and the hash before it.</summary>
public sealed record EnvelopeEvidence(StoredEnvelope Envelope, StoredCountersignature? Countersignature, LedgerEntry? LedgerEntry, string? PreviousEntryHash);

/// <summary>Reads an envelope's evidence in the current tenant (row-level security scopes it).</summary>
public interface IEnvelopeEvidence
{
    Task<EnvelopeEvidence?> FindAsync(Guid envelopeId, CancellationToken cancellationToken);
}

/// <summary>One check of the verification and its result: <c>pass</c>, <c>fail</c>, <c>pending</c> or <c>not_checked</c>.</summary>
public sealed record EnvelopeCheck(string Name, string Result, string? Detail = null);

/// <summary>
/// The verification of one envelope (US-P2-SIG-04): <c>verified</c> when every check passes, <c>pending</c> while only
/// the countersignature is missing, <c>invalid</c> when any check fails.
/// </summary>
public sealed record EnvelopeVerification(
    Guid EnvelopeId, string RecordType, Guid RecordId, int RecordVersion, string EnvelopeHash, DateTimeOffset ReceivedAt, string Status,
    string? ServerKeyId, DateTimeOffset? CountersignedAt, long? LedgerSequence, IReadOnlyList<EnvelopeCheck> Checks);

/// <summary>
/// Verifies a stored envelope from the database alone (ADR-0033 §6), each check independently, so a broken one names
/// what is wrong: the envelope text and its hash; the device signature with the tablet's key; the server
/// countersignature with its published key; the ledger entry that binds the receipt time, recomputed and linked to the
/// entry before it. The record's own payload hash is checked by the record's module once records exist (P2.4).
/// </summary>
public sealed class VerifyEnvelope(IEnvelopeEvidence evidence, IDeviceDirectory devices)
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Pending = "pending";
    public const string NotChecked = "not_checked";

    public async Task<EnvelopeVerification?> RunAsync(Guid envelopeId, CancellationToken cancellationToken)
    {
        if (await evidence.FindAsync(envelopeId, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        var stored = found.Envelope;
        using var document = Parse(stored.Canonical);
        var checks = new List<EnvelopeCheck>
        {
            EnvelopeText(stored, document),
            await DeviceSignatureAsync(stored, document, cancellationToken).ConfigureAwait(false),
            Countersignature(stored, found.Countersignature),
            Ledger(stored, found.LedgerEntry, found.PreviousEntryHash),
            new("record_payload", NotChecked, "Checked by the record's module once records exist (P2.4)."),
        };
        var status = checks.Any(c => c.Result == Fail) ? "invalid" : checks.Any(c => c.Result == Pending) ? "pending" : "verified";
        return new EnvelopeVerification(
            stored.EnvelopeId, stored.RecordType, stored.RecordId, stored.RecordVersion, stored.EnvelopeHash, stored.ReceivedAt, status,
            found.Countersignature?.KeyId, found.Countersignature?.SignedAt, found.LedgerEntry?.Sequence, checks);
    }

    private static JsonDocument? Parse(string canonical)
    {
        try
        {
            return JsonDocument.Parse(canonical);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The stored text is a valid envelope v1, in canonical form, with the stored hash.</summary>
    private static EnvelopeCheck EnvelopeText(StoredEnvelope stored, JsonDocument? document)
    {
        const string name = "envelope";
        if (document is null || SignatureEnvelopes.Problem(document.RootElement) is not null)
        {
            return new(name, Fail, "The stored envelope is not a valid envelope v1.");
        }

        var canonical = SignatureEnvelopes.Canonical(document.RootElement);
        if (!Encoding.UTF8.GetBytes(stored.Canonical).AsSpan().SequenceEqual(canonical))
        {
            return new(name, Fail, "The stored text is not in canonical form.");
        }

        return SignatureEnvelopes.Hash(document.RootElement) == stored.EnvelopeHash
            ? new(name, Pass)
            : new(name, Fail, "The stored hash is not the hash of the stored text.");
    }

    private async Task<EnvelopeCheck> DeviceSignatureAsync(StoredEnvelope stored, JsonDocument? document, CancellationToken cancellationToken)
    {
        const string name = "device_signature";
        if (document is null)
        {
            return new(name, Fail, "The stored envelope cannot be read.");
        }

        // The tablet's key does not change while it is enrolled (a re-enrolled tablet is a new device), and a revoked
        // tablet's key still verifies what it signed before.
        if (await devices.FindAsync(stored.DeviceId, cancellationToken).ConfigureAwait(false) is not { } key)
        {
            return new(name, Fail, "The tablet that signed is not known.");
        }

        return SignatureEnvelopes.VerifyDevice(document.RootElement, key.X, key.Y, stored.DeviceSignature)
            ? new(name, Pass)
            : new(name, Fail, "The tablet's signature does not match the envelope.");
    }

    private static EnvelopeCheck Countersignature(StoredEnvelope stored, StoredCountersignature? countersignature)
    {
        const string name = "server_countersignature";
        if (countersignature is null)
        {
            return new(name, Pending, "Not countersigned yet (target 5 minutes).");
        }

        if (countersignature.PublicKeyPem is null)
        {
            return new(name, Fail, $"Server key {countersignature.KeyId} is not published.");
        }

        var digest = Countersignatures.Digest(stored.EnvelopeHash, stored.ReceivedAt);
        var signature = Base64Url.IsValid(countersignature.Signature) ? Base64Url.DecodeFromChars(countersignature.Signature) : [];
        return ServerSignatures.Verify(countersignature.PublicKeyPem, digest, signature)
            ? new(name, Pass)
            : new(name, Fail, "The server countersignature does not match.");
    }

    /// <summary>The ledger entry naming this envelope's hash, recomputed, and linked to the entry before it.</summary>
    private static EnvelopeCheck Ledger(StoredEnvelope stored, LedgerEntry? entry, string? previousEntryHash)
    {
        const string name = "ledger_entry";
        if (entry is null)
        {
            return new(name, Fail, "No ledger entry records this envelope.");
        }

        string canonical;
        try
        {
            canonical = LedgerHash.CanonicalPayload(entry.Payload);
        }
        catch (FormatException)
        {
            return new(name, Fail, "The ledger entry's payload is not canonical JSON.");
        }

        using var payload = JsonDocument.Parse(canonical);
        var names = payload.RootElement.TryGetProperty("envelopeHash", out var hash) && hash.GetString() == stored.EnvelopeHash;
        var intact = canonical == entry.Payload && LedgerHash.OfPayload(entry.Payload) == entry.PayloadHash
            && LedgerHash.OfEntry(entry.PreviousHash, entry.Tenant, entry.Sequence, entry.EventType, entry.PayloadHash, entry.OccurredAt) == entry.EntryHash;
        var linked = entry.Sequence == 1 ? entry.PreviousHash == LedgerHash.Genesis : entry.PreviousHash == previousEntryHash;
        return (names, intact, linked) switch
        {
            (false, _, _) => new(name, Fail, "The ledger entry names another envelope."),
            (_, false, _) => new(name, Fail, "The ledger entry does not match its hash."),
            (_, _, false) => new(name, Fail, "The ledger entry is not linked to the entry before it."),
            _ => new(name, Pass),
        };
    }
}
