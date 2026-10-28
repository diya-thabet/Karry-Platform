using System.Globalization;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Sync.Domain;

/// <summary>A command's ID, chosen by the tablet when it records the command (UUIDv7).</summary>
public readonly record struct CommandId(Guid Value) : IEntityId<CommandId>
{
    public static CommandId From(Guid value) => new(value);
}

public enum InboxStatus
{
    /// <summary>Stored in the device's sequence, waiting for its handler (Phase 2).</summary>
    Received,

    /// <summary>Sent by a revoked device: kept for review, never processed (ADR-0028 §3).</summary>
    Quarantined,
}

/// <summary>
/// What a tablet signed: <see cref="Body"/> is the RFC 8785 canonical JSON of the command without its signature (the
/// exact bytes the signature covers, kept so the command can be verified again at any time).
/// </summary>
public sealed record SignedCommand(CommandId Id, long Sequence, string Type, DateTimeOffset DeviceTime, string Body, string Signature);

/// <summary>
/// One command a tablet uploaded (ADR-0028 §6), in schema <c>sync</c>. Immutable once stored: Phase 2 handlers add
/// their outcome next to it, the command itself never changes.
/// </summary>
public sealed class InboxCommand : ITenantOwned
{
    private InboxCommand(TenantId tenantId, Guid deviceId, SignedCommand command, InboxStatus status, DateTimeOffset receivedAt)
    {
        Id = command.Id;
        TenantId = tenantId;
        DeviceId = deviceId;
        Sequence = command.Sequence;
        Type = command.Type;
        DeviceTime = command.DeviceTime;
        Body = command.Body;
        Signature = command.Signature;
        Status = status;
        ReceivedAt = receivedAt;
    }

    // EF Core
    private InboxCommand()
    {
    }

    public CommandId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid DeviceId { get; private set; }

    /// <summary>The device's own counter: 1, 2, 3… without gaps among received commands.</summary>
    public long Sequence { get; private set; }

    public string Type { get; private set; } = null!;

    /// <summary>When the tablet recorded the command, by its own (corrected) clock.</summary>
    public DateTimeOffset DeviceTime { get; private set; }

    public string Body { get; private set; } = null!;

    /// <summary>ES256 over <see cref="Body"/>, IEEE P1363, base64url.</summary>
    public string Signature { get; private set; } = null!;

    public InboxStatus Status { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public static InboxCommand Receive(TenantId tenant, Guid deviceId, SignedCommand command, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new InboxCommand(tenant, deviceId, command, InboxStatus.Received, at);
    }

    public static InboxCommand Quarantine(TenantId tenant, Guid deviceId, SignedCommand command, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new InboxCommand(tenant, deviceId, command, InboxStatus.Quarantined, at);
    }
}

public enum UploadOutcome
{
    /// <summary>Stored now.</summary>
    Accepted,

    /// <summary>Already stored (a batch sent again after a lost answer): the tablet marks it sent.</summary>
    Duplicate,

    /// <summary>Not stored; <see cref="UploadDecision.Code"/> says why. The tablet keeps it.</summary>
    Rejected,
}

/// <summary>A command of a batch, as far as the decision needs it.</summary>
public sealed record UploadCandidate(CommandId Id, long Sequence, bool SignatureValid);

public sealed record UploadDecision(CommandId Id, UploadOutcome Outcome, string? Code);

/// <summary>The rules of an upload (ADR-0028 §6): command shape, duplicates and the gap-free sequence of a device.</summary>
public static partial class InboxRules
{
    public const int MaxCommands = 100;

    /// <summary>The largest canonical command, in UTF-8 bytes.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    public const int TypeMaxLength = 100;

    public const string SignatureInvalid = "sync.signature_invalid";

    /// <summary>A command whose sequence is past the next expected one: an earlier command is missing.</summary>
    public const string SequenceGap = "sync.sequence_gap";

    /// <summary>A new command with a sequence number another command already holds.</summary>
    public const string SequenceReused = "sync.sequence_reused";

    /// <summary>A versioned dotted name, e.g. <c>production.trip_recorded.v1</c>.</summary>
    public static bool IsCommandType(string? type) => type is not null && type.Length <= TypeMaxLength && CommandTypePattern().IsMatch(type);

    /// <summary>A UUIDv7 in its lowercase text form, the only form a tablet sends (so the signed text is unambiguous).</summary>
    public static bool TryParseCommandId(string? text, out CommandId id)
    {
        id = default;
        if (text is null || !LowercaseUuidPattern().IsMatch(text) || !Guid.TryParseExact(text, "D", out var value) || value.Version != 7)
        {
            return false;
        }

        id = new CommandId(value);
        return true;
    }

    /// <summary>An ISO 8601 date and time with an explicit offset (<c>Z</c> from <c>toISOString()</c>).</summary>
    public static bool TryParseDeviceTime(string? text, out DateTimeOffset at)
    {
        at = default;
        return text is not null && DeviceTimePattern().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }

    /// <summary>
    /// Decides each command in batch order, given the device's last received sequence and the IDs already stored. A
    /// known ID is a duplicate; else a valid signature and the next sequence number are required. A rejected command
    /// does not advance the sequence, so the commands after it are gaps: the tablet sends them again once it is fixed.
    /// </summary>
    public static (IReadOnlyList<UploadDecision> Decisions, long LastSequence) Decide(
        long lastSequence, IReadOnlySet<CommandId> stored, IEnumerable<UploadCandidate> commands)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(commands);
        var accepted = new HashSet<CommandId>();
        var decisions = new List<UploadDecision>();
        foreach (var command in commands)
        {
            if (stored.Contains(command.Id) || accepted.Contains(command.Id))
            {
                decisions.Add(new UploadDecision(command.Id, UploadOutcome.Duplicate, null));
            }
            else if (!command.SignatureValid)
            {
                decisions.Add(new UploadDecision(command.Id, UploadOutcome.Rejected, SignatureInvalid));
            }
            else if (command.Sequence == lastSequence + 1)
            {
                lastSequence = command.Sequence;
                accepted.Add(command.Id);
                decisions.Add(new UploadDecision(command.Id, UploadOutcome.Accepted, null));
            }
            else
            {
                decisions.Add(new UploadDecision(command.Id, UploadOutcome.Rejected, command.Sequence <= lastSequence ? SequenceReused : SequenceGap));
            }
        }

        return (decisions, lastSequence);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+\\.v[1-9][0-9]*$")]
    private static partial Regex CommandTypePattern();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex LowercaseUuidPattern();

    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(\\.\\d{1,7})?(Z|[+-]\\d{2}:\\d{2})$")]
    private static partial Regex DeviceTimePattern();
}
