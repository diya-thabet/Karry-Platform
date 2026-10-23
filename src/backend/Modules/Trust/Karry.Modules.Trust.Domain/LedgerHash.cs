using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Karry.SharedKernel.Canonical;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Trust.Domain;

/// <summary>
/// The ledger's hash chain (ADR-0008, US-P1-AUD-01), specified exactly so any auditor can recompute it:
/// <code>
/// payload_hash = hex(SHA-256(RFC 8785 canonical payload, UTF-8))
/// entry_hash   = hex(SHA-256(UTF-8(prev_hash "\n" tenant_id "\n" seq "\n" event_type "\n" payload_hash "\n" occurred_at)))
/// </code>
/// Hashes are lowercase hex (64 characters); <c>tenant_id</c> is the lowercase hyphenated UUID; <c>seq</c> is decimal
/// without leading zeros, starting at 1; <c>occurred_at</c> is UTC <c>yyyy-MM-ddTHH:mm:ss.ffffffZ</c> (microseconds,
/// PostgreSQL's precision). The first entry's <c>prev_hash</c> is <see cref="Genesis"/>. The newline separator
/// cannot occur inside any field, so the encoding is unambiguous.
/// </summary>
public static class LedgerHash
{
    public static readonly string Genesis = new('0', 64);

    public static string OfPayload(string canonicalPayload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload)));

    public static string OfEntry(string previousHash, TenantId tenant, long sequence, string eventType, string payloadHash, DateTimeOffset occurredAt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        if (eventType.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Event type cannot contain a newline.", nameof(eventType));
        }

        var material = string.Join('\n',
            previousHash,
            tenant.Value.ToString("D"),
            sequence.ToString(CultureInfo.InvariantCulture),
            eventType,
            payloadHash,
            Timestamp(occurredAt));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Canonical payload text stored in the ledger (strict RFC 8785: exact numbers only).</summary>
    public static string CanonicalPayload(string json) => JsonCanonicalizer.CanonicalText(json);

    /// <summary>Truncated to whole microseconds, as stored by PostgreSQL: the hash must be recomputable from the row.</summary>
    public static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    public static string Timestamp(DateTimeOffset value) =>
        ToStoragePrecision(value).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>One stored ledger entry, as the verifier reads it.</summary>
public sealed record LedgerEntry(
    TenantId Tenant, long Sequence, Guid EventId, string EventType, DateTimeOffset OccurredAt,
    string Payload, string PayloadHash, string PreviousHash, string EntryHash);

/// <summary>Result of recomputing a tenant's chain.</summary>
public sealed record ChainVerification(bool IsValid, long EntriesChecked, long? FirstBrokenSequence, string? Problem, string? HeadHash)
{
    public static ChainVerification Valid(long count, string? head) => new(true, count, null, null, head);

    public static ChainVerification Broken(long count, long sequence, string problem) => new(false, count, sequence, problem, null);
}

/// <summary>Recomputes a chain from stored entries (in sequence order): the core of the verifier (US-P1-AUD-03).</summary>
public static class LedgerVerifier
{
    public static ChainVerification Verify(IEnumerable<LedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var previous = LedgerHash.Genesis;
        long expected = 1;
        foreach (var entry in entries)
        {
            if (entry.Sequence != expected)
            {
                return ChainVerification.Broken(expected - 1, expected, $"sequence {expected} is missing (found {entry.Sequence})");
            }

            string canonical;
            try
            {
                canonical = LedgerHash.CanonicalPayload(entry.Payload);
            }
            catch (FormatException ex)
            {
                return ChainVerification.Broken(expected - 1, entry.Sequence, $"payload is not canonical JSON: {ex.Message}");
            }

            if (!string.Equals(canonical, entry.Payload, StringComparison.Ordinal)
                || !string.Equals(LedgerHash.OfPayload(entry.Payload), entry.PayloadHash, StringComparison.Ordinal))
            {
                return ChainVerification.Broken(expected - 1, entry.Sequence, "payload does not match its hash");
            }

            if (!string.Equals(entry.PreviousHash, previous, StringComparison.Ordinal))
            {
                return ChainVerification.Broken(expected - 1, entry.Sequence, "previous hash does not link to the entry before");
            }

            var recomputed = LedgerHash.OfEntry(previous, entry.Tenant, entry.Sequence, entry.EventType, entry.PayloadHash, entry.OccurredAt);
            if (!string.Equals(recomputed, entry.EntryHash, StringComparison.Ordinal))
            {
                return ChainVerification.Broken(expected - 1, entry.Sequence, "entry hash does not match its content");
            }

            previous = entry.EntryHash;
            expected++;
        }

        return ChainVerification.Valid(expected - 1, expected == 1 ? null : previous);
    }
}
