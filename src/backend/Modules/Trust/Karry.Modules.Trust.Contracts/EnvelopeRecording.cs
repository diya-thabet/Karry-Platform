using System.Data.Common;
using System.Text.Json;

namespace Karry.Modules.Trust.Contracts;

/// <summary>
/// A signature envelope as it arrives with a tablet's command (ADR-0033 §4): the envelope v1, the device's signature
/// over its canonical text, and the command and device it came with.
/// </summary>
public sealed record EnvelopeSubmission(Guid TenantId, Guid DeviceId, Guid CommandId, JsonElement Envelope, string DeviceSignature, DateTimeOffset ReceivedAt);

/// <summary>What became of a submitted envelope.</summary>
public abstract record EnvelopeOutcome
{
    private EnvelopeOutcome()
    {
    }

    /// <summary>Stored, with its ledger entry; countersigning follows (state <c>pending</c>).</summary>
    public sealed record Recorded(string EnvelopeHash) : EnvelopeOutcome;

    /// <summary>The same envelope was already stored (a command sent again): nothing new is written.</summary>
    public sealed record AlreadyRecorded(string EnvelopeHash) : EnvelopeOutcome;

    /// <summary>
    /// Refused for good, nothing stored: <see cref="EnvelopeCodes"/>. The command handler rejects the command with
    /// this code (final, kept in its place in the sequence, ADR-0031 §3).
    /// </summary>
    public sealed record Refused(string Code) : EnvelopeOutcome;
}

public static class EnvelopeCodes
{
    /// <summary>The envelope breaks the v1 rules (<see cref="SignatureEnvelopes.Problem"/>).</summary>
    public const string Invalid = "trust.envelope_invalid";

    /// <summary>The envelope names another device than the one that sent the command.</summary>
    public const string DeviceMismatch = "trust.envelope_device_mismatch";

    /// <summary>The device signature does not verify with the device's key (TC-P2-SIG-002): a security event.</summary>
    public const string SignatureInvalid = "trust.envelope_signature_invalid";

    /// <summary>
    /// The tablet was revoked (TC-P2-SIG-003): nothing it sends is accepted any more, even when its signature verifies.
    /// Device authentication already refuses a revoked tablet's requests; this holds even if a caller skipped it.
    /// </summary>
    public const string DeviceRevoked = "trust.envelope_device_revoked";

    /// <summary>An envelope with this ID but other content is already stored: a security event.</summary>
    public const string Conflict = "trust.envelope_conflict";
}

/// <summary>
/// Stores a record's signature envelope in the command's transaction (ADR-0033 §4): it checks the envelope and the
/// device signature, writes it append-only, and writes its ledger entry, which binds the server's receipt time to
/// the envelope hash at once. A handler calls it from inside <c>ISyncCommandHandler.HandleAsync</c>; it never commits.
/// </summary>
public interface IEnvelopeRecorder
{
    Task<EnvelopeOutcome> RecordAsync(DbConnection connection, DbTransaction transaction, EnvelopeSubmission submission, CancellationToken cancellationToken);
}

/// <summary>The ledger entry written when an envelope is stored: it binds the envelope hash to the receipt time.</summary>
public static class EnvelopeLedger
{
    public const string ReceivedEvent = "trust.envelope_received.v1";
}
