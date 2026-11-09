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

    /// <summary>Stored now and flagged for review (<see cref="UploadDecision.Flags"/>); final, the tablet marks it sent.</summary>
    Conflict,
}

/// <summary>A command of a batch, as far as the decision needs it.</summary>
public sealed record UploadCandidate(CommandId Id, long Sequence, bool SignatureValid);

/// <summary>
/// What happened to one command. A rejection is <see cref="Retryable"/> when sending the same command later can
/// succeed (a missing earlier command, a type the server learns in an update, a handler failure).
/// </summary>
public sealed record UploadDecision(CommandId Id, UploadOutcome Outcome, string? Code, bool Retryable = false, IReadOnlyList<string>? Flags = null)
{
    public static UploadDecision Accepted(CommandId id) => new(id, UploadOutcome.Accepted, null);

    public static UploadDecision Rejected(CommandId id, string code, bool retryable) => new(id, UploadOutcome.Rejected, code, retryable);

    /// <summary>Stored in the device's sequence (accepted or flagged): the next number is due.</summary>
    public bool Stored => Outcome is UploadOutcome.Accepted or UploadOutcome.Conflict;
}

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

    /// <summary>No handler for the command's type (an app newer than the server): kept, and handled when sent again after an update.</summary>
    public const string TypeUnknown = "sync.type_unknown";

    /// <summary>The handler failed; nothing of the command was kept, and sending it again is safe.</summary>
    public const string HandlerFailed = "sync.handler_failed";

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
    /// known ID is a duplicate; else a valid signature and the next sequence number are required, and then
    /// <paramref name="handle"/> decides (ADR-0031 §2; it gets the command's index in the batch). Only a stored command advances the sequence: after a rejected
    /// one the commands are gaps, and the tablet sends them again once the earlier one is through.
    /// </summary>
    public static async Task<(IReadOnlyList<UploadDecision> Decisions, long LastSequence)> DecideAsync(
        long lastSequence, IReadOnlySet<CommandId> stored, IEnumerable<UploadCandidate> commands, Func<UploadCandidate, int, Task<UploadDecision>> handle)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(handle);
        var accepted = new HashSet<CommandId>();
        var decisions = new List<UploadDecision>();
        var index = -1;
        foreach (var command in commands)
        {
            index++;
            if (stored.Contains(command.Id) || accepted.Contains(command.Id))
            {
                decisions.Add(new UploadDecision(command.Id, UploadOutcome.Duplicate, null));
            }
            else if (!command.SignatureValid)
            {
                decisions.Add(UploadDecision.Rejected(command.Id, SignatureInvalid, retryable: false));
            }
            else if (command.Sequence != lastSequence + 1)
            {
                decisions.Add(command.Sequence <= lastSequence
                    ? UploadDecision.Rejected(command.Id, SequenceReused, retryable: false)
                    : UploadDecision.Rejected(command.Id, SequenceGap, retryable: true));
            }
            else
            {
                var decision = await handle(command, index).ConfigureAwait(false);
                if (decision.Stored)
                {
                    lastSequence = command.Sequence;
                    accepted.Add(command.Id);
                }

                decisions.Add(decision);
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

/// <summary>
/// What the handler made of a stored command (ADR-0031 §2), written in the same transaction as the handler's rows.
/// Append-only: a later review changes the record, not this row.
/// </summary>
public sealed class ProcessedCommand : ITenantOwned
{
    public ProcessedCommand(TenantId tenantId, Guid deviceId, CommandId commandId, UploadOutcome outcome, Guid? recordId, IReadOnlyList<string> flags, DateTimeOffset handledAt)
    {
        ArgumentNullException.ThrowIfNull(flags);
        if (outcome is not (UploadOutcome.Accepted or UploadOutcome.Conflict))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Only a stored command is processed.");
        }

        TenantId = tenantId;
        DeviceId = deviceId;
        CommandId = commandId;
        Outcome = outcome;
        RecordId = recordId;
        Flags = [.. flags];
        HandledAt = handledAt;
    }

    // EF Core
    private ProcessedCommand()
    {
    }

    public TenantId TenantId { get; private set; }

    public Guid DeviceId { get; private set; }

    public CommandId CommandId { get; private set; }

    public UploadOutcome Outcome { get; private set; }

    /// <summary>The record the command created or changed, when there is one.</summary>
    public Guid? RecordId { get; private set; }

    public List<string> Flags { get; private set; } = [];

    public DateTimeOffset HandledAt { get; private set; }
}

/// <summary>
/// A command whose type the server has no handler for (ADR-0031 §5: a newer app talking to an older server). Kept as
/// the tablet signed it, outside the device's sequence, so nothing is lost; the tablet sends it again after the
/// update, and then it is handled. Append-only.
/// </summary>
public sealed class UnhandledCommand : ITenantOwned
{
    public UnhandledCommand(TenantId tenantId, Guid deviceId, SignedCommand command, string reason, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(command);
        TenantId = tenantId;
        DeviceId = deviceId;
        Id = command.Id;
        Sequence = command.Sequence;
        Type = command.Type;
        DeviceTime = command.DeviceTime;
        Body = command.Body;
        Signature = command.Signature;
        Reason = reason;
        ReceivedAt = receivedAt;
    }

    // EF Core
    private UnhandledCommand()
    {
    }

    public CommandId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid DeviceId { get; private set; }

    public long Sequence { get; private set; }

    public string Type { get; private set; } = null!;

    public DateTimeOffset DeviceTime { get; private set; }

    public string Body { get; private set; } = null!;

    public string Signature { get; private set; } = null!;

    /// <summary>Why it was not handled, e.g. <see cref="InboxRules.TypeUnknown"/>.</summary>
    public string Reason { get; private set; } = null!;

    public DateTimeOffset ReceivedAt { get; private set; }
}
