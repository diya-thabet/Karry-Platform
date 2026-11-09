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

    /// <summary>
    /// Kept, waiting for an earlier number (ADR-0031 §4): the server has it, so the tablet does not send it again, but
    /// keeps its evidence until the final answer comes through the status pull.
    /// </summary>
    Waiting,
}

/// <summary>
/// A command as far as the decision needs it. <see cref="Index"/> is its place in the batch, or -1 for a command that
/// was already waiting from an earlier upload.
/// </summary>
public sealed record UploadCandidate(CommandId Id, long Sequence, bool SignatureValid, int Index = -1);

/// <summary>
/// The decisions of a batch, in its order, and what follows from them: the device's last sequence, the numbers the
/// tablet must send again (<see cref="MissingSequences"/>), the batch's commands to keep as waiting
/// (<see cref="Held"/>), and the outcomes of earlier waiting commands handled now (<see cref="Promoted"/>).
/// </summary>
public sealed record UploadPlan(
    IReadOnlyList<UploadDecision> Decisions, long LastSequence, IReadOnlyList<long> MissingSequences, IReadOnlyList<UploadCandidate> Held,
    IReadOnlyList<UploadDecision> Promoted);

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

    /// <summary>
    /// A command whose sequence is past the next expected one, before P2.1 slice 2. Gaps are now held as waiting; the code
    /// stays reserved so an old answer is never reused with another meaning.
    /// </summary>
    public const string SequenceGap = "sync.sequence_gap";

    /// <summary>A new command with a sequence number another command already holds.</summary>
    public const string SequenceReused = "sync.sequence_reused";

    /// <summary>No handler for the command's type (an app newer than the server): kept, and handled when sent again after an update.</summary>
    public const string TypeUnknown = "sync.type_unknown";

    /// <summary>More than <see cref="MaxWaiting"/> commands would wait for an earlier number: send the missing ones first (threat T-12).</summary>
    public const string TooFarAhead = "sync.too_far_ahead";

    /// <summary>The most commands of one device waiting for an earlier number.</summary>
    public const int MaxWaiting = 500;

    /// <summary>The most missing numbers listed in one answer; the tablet asks again after sending them.</summary>
    public const int MaxMissingListed = 100;

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
    /// Decides each command in batch order (ADR-0031 §2, §4), given the device's last received sequence, the IDs already
    /// stored and the commands already waiting for an earlier number. A known ID is a duplicate (or still waiting); else
    /// a valid signature is required. The next number goes to <paramref name="handle"/>; a number further on is held
    /// as waiting (at most <see cref="MaxWaiting"/> per device), and handled as soon as the numbers before it are in,
    /// in the same request. Only a stored command advances the sequence.
    /// </summary>
    public static async Task<UploadPlan> DecideAsync(
        long lastSequence, IReadOnlySet<CommandId> stored, IReadOnlyCollection<UploadCandidate> waiting, IEnumerable<UploadCandidate> commands,
        Func<UploadCandidate, Task<UploadDecision>> handle)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(handle);
        var accepted = new HashSet<CommandId>();
        var held = new SortedDictionary<long, UploadCandidate>();
        foreach (var command in waiting.Where(w => w.Sequence > lastSequence && !stored.Contains(w.Id)))
        {
            held.TryAdd(command.Sequence, command);
        }

        var heldIds = held.Values.Select(h => h.Id).ToHashSet();
        var decisions = new List<UploadDecision>();
        var promoted = new List<UploadDecision>();
        var finals = new Dictionary<CommandId, UploadDecision>();
        var index = -1;
        foreach (var listed in commands)
        {
            var command = listed with { Index = ++index };
            if (stored.Contains(command.Id) || accepted.Contains(command.Id))
            {
                decisions.Add(new UploadDecision(command.Id, UploadOutcome.Duplicate, null));
            }
            else if (heldIds.Contains(command.Id))
            {
                decisions.Add(new UploadDecision(command.Id, UploadOutcome.Waiting, null));
            }
            else if (!command.SignatureValid)
            {
                decisions.Add(UploadDecision.Rejected(command.Id, SignatureInvalid, retryable: false));
            }
            else if (command.Sequence <= lastSequence || held.ContainsKey(command.Sequence))
            {
                decisions.Add(UploadDecision.Rejected(command.Id, SequenceReused, retryable: false));
            }
            else if (command.Sequence > lastSequence + 1)
            {
                if (held.Count >= MaxWaiting)
                {
                    decisions.Add(UploadDecision.Rejected(command.Id, TooFarAhead, retryable: true));
                }
                else
                {
                    held.Add(command.Sequence, command);
                    heldIds.Add(command.Id);
                    decisions.Add(new UploadDecision(command.Id, UploadOutcome.Waiting, null));
                }
            }
            else
            {
                var decision = await handle(command).ConfigureAwait(false);
                decisions.Add(decision);
                if (decision.Stored)
                {
                    lastSequence = command.Sequence;
                    accepted.Add(command.Id);
                    await DrainAsync().ConfigureAwait(false);
                }
            }
        }

        var missing = new List<long>();
        if (held.Count > 0)
        {
            for (var n = lastSequence + 1; n < held.Keys.Max() && missing.Count < MaxMissingListed; n++)
            {
                if (!held.ContainsKey(n))
                {
                    missing.Add(n);
                }
            }
        }

        // A command answered "waiting" earlier in the batch may have been handled since: answer what became of it.
        var answered = decisions.Select(d => d.Outcome == UploadOutcome.Waiting && finals.TryGetValue(d.Id, out var final) ? final : d).ToList();
        return new UploadPlan(answered, lastSequence, missing, [.. held.Values.Where(h => h.Index >= 0)], promoted);

        // The commands held for the numbers just reached, in order; one that is not stored stops the run.
        async Task DrainAsync()
        {
            while (held.TryGetValue(lastSequence + 1, out var next))
            {
                var decision = await handle(next).ConfigureAwait(false);
                finals[next.Id] = decision;
                if (next.Index >= 0)
                {
                    decisions[next.Index] = decision;
                }
                else
                {
                    promoted.Add(decision);
                }

                if (!decision.Stored)
                {
                    if (next.Index >= 0)
                    {
                        // Sent in this batch and answered rejected: not kept as waiting, the tablet sends it again.
                        held.Remove(next.Sequence);
                        heldIds.Remove(next.Id);
                    }

                    return;
                }

                held.Remove(next.Sequence);
                lastSequence = next.Sequence;
                accepted.Add(next.Id);
            }
        }
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

/// <summary>
/// A command that came before an earlier number of its device (ADR-0031 §4): kept as the tablet signed it, outside the
/// sequence, and handled as soon as the numbers before it are in. A row whose command is in the inbox has been handled.
/// Append-only.
/// </summary>
public sealed class WaitingCommand : ITenantOwned
{
    public WaitingCommand(TenantId tenantId, Guid deviceId, SignedCommand command, DateTimeOffset receivedAt, int clockOffsetMs)
    {
        ArgumentNullException.ThrowIfNull(command);
        ClockOffsetMs = clockOffsetMs;
        TenantId = tenantId;
        DeviceId = deviceId;
        Id = command.Id;
        Sequence = command.Sequence;
        Type = command.Type;
        DeviceTime = command.DeviceTime;
        Body = command.Body;
        Signature = command.Signature;
        ReceivedAt = receivedAt;
    }

    // EF Core
    private WaitingCommand()
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

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>The tablet's clock offset measured when the command came (ADR-0031 §6): its handler, later, gets this one.</summary>
    public int ClockOffsetMs { get; private set; }

    public SignedCommand Command => new(Id, Sequence, Type, DeviceTime, Body, Signature);
}

/// <summary>
/// A tablet's clock against the server's (ADR-0031 §6), measured at each upload from the time the tablet signed the
/// request: positive when the tablet runs ahead. The last measurement is kept; it is not evidence, so it is updated.
/// </summary>
public sealed class DeviceClock : ITenantOwned
{
    public DeviceClock(TenantId tenantId, Guid deviceId, int offsetMs, DateTimeOffset measuredAt)
    {
        TenantId = tenantId;
        DeviceId = deviceId;
        Measure(offsetMs, measuredAt);
    }

    // EF Core
    private DeviceClock()
    {
    }

    public TenantId TenantId { get; private set; }

    public Guid DeviceId { get; private set; }

    public int OffsetMs { get; private set; }

    public DateTimeOffset MeasuredAt { get; private set; }

    public void Measure(int offsetMs, DateTimeOffset measuredAt)
    {
        OffsetMs = offsetMs;
        MeasuredAt = measuredAt;
    }

    /// <summary>The offset of a request signed at <paramref name="signedAt"/> and received at <paramref name="receivedAt"/>, in milliseconds.</summary>
    public static int OffsetOf(DateTimeOffset signedAt, DateTimeOffset receivedAt) =>
        (int)Math.Clamp(Math.Round((signedAt - receivedAt).TotalMilliseconds), int.MinValue, int.MaxValue);
}

/// <summary>
/// An evidence blob a stored command references (ADR-0032): the registry of which command uses which content, kept with
/// the command. The blob itself is in the evidence bucket, under the key its tenant, device and hash make. Append-only.
/// </summary>
public sealed class CommandEvidence : ITenantOwned
{
    public CommandEvidence(TenantId tenantId, Guid deviceId, CommandId commandId, string sha256, long size, string contentType, DateTimeOffset recordedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        TenantId = tenantId;
        DeviceId = deviceId;
        CommandId = commandId;
        Sha256 = sha256;
        Size = size;
        ContentType = contentType;
        RecordedAt = recordedAt;
    }

    // EF Core
    private CommandEvidence()
    {
    }

    public TenantId TenantId { get; private set; }

    public Guid DeviceId { get; private set; }

    public CommandId CommandId { get; private set; }

    /// <summary>The lowercase hex SHA-256 of the exact bytes (the last part of the key).</summary>
    public string Sha256 { get; private set; } = string.Empty;

    public long Size { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public DateTimeOffset RecordedAt { get; private set; }
}
