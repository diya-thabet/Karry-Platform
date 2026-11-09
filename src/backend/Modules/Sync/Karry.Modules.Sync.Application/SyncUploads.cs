using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Storage;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Sync.Domain;
using Karry.SharedKernel.Canonical;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Sync.Application;

/// <summary>
/// A command as the tablet's outbox holds it (ADR-0028 §6, <c>@karry/offline</c> <c>OutboxRecord</c>):
/// <paramref name="Signature"/> is ES256 (IEEE P1363, base64url) over the RFC 8785 form of the other five members.
/// </summary>
public sealed record UploadedCommand(string? CommandId, long Sequence, string? Type, JsonElement Payload, string? DeviceTime, string? Signature);

public sealed record UploadBatch(IReadOnlyList<UploadedCommand>? Commands);

/// <summary>
/// What happened to one command: <c>accepted</c>, <c>conflict</c> (stored and flagged, <see cref="Flags"/>),
/// <c>duplicate</c>, <c>waiting</c> (kept until an earlier number comes) or <c>rejected</c> (with <see cref="Code"/>;
/// <see cref="Retryable"/> when sending it again later can succeed). Every status but <c>rejected</c> means the server
/// has the command: the tablet marks it sent.
/// </summary>
public sealed record CommandReceipt(Guid CommandId, string Status, string? Code, bool Retryable, IReadOnlyList<string> Flags);

/// <summary>
/// The answer to a batch, in its order; <see cref="LastSequence"/> is the device's last stored sequence number and
/// <see cref="ServerTime"/> the server's clock when it answered (the tablet measures its offset from it, ADR-0031 §6).
/// </summary>
/// <see cref="MissingSequences"/> lists the numbers to send again so that waiting commands can be handled (ADR-0031 §4).
public sealed record BatchReceipt(IReadOnlyList<CommandReceipt> Results, long LastSequence, IReadOnlyList<long> MissingSequences, DateTimeOffset ServerTime);

/// <summary>
/// One upload of a tablet, in one database transaction (ADR-0031 §2). Opening it takes the tablet's lock, so crossing
/// uploads of one tablet run one after the other and each decides against what the previous one stored.
/// </summary>
public interface IInboxSession : IAsyncDisposable
{
    /// <summary>The batch's commands the device has already uploaded (received or quarantined).</summary>
    IReadOnlySet<CommandId> Stored { get; }

    /// <summary>The batch's commands already kept as unhandled (their type was unknown when they came).</summary>
    IReadOnlySet<CommandId> Unhandled { get; }

    /// <summary>The highest sequence number among the device's received commands; 0 before its first.</summary>
    long LastSequence { get; }

    /// <summary>The device's commands waiting for an earlier number (at most <see cref="InboxRules.MaxWaiting"/>).</summary>
    IReadOnlyList<WaitingCommand> Waiting { get; }

    /// <summary>The transaction command handlers join.</summary>
    ISyncTransaction Transaction { get; }

    Task SavepointAsync(string name, CancellationToken cancellationToken);

    Task ReleaseAsync(string name, CancellationToken cancellationToken);

    /// <summary>Undoes everything done since the savepoint, also after a failed statement.</summary>
    Task RollbackToAsync(string name, CancellationToken cancellationToken);

    void Add(InboxCommand command);

    void Add(ProcessedCommand processed);

    void Add(UnhandledCommand unhandled);

    void Add(WaitingCommand waiting);

    void Add(CommandEvidence evidence);

    /// <summary>Keeps the tablet's latest clock offset (ADR-0031 §6).</summary>
    void MeasureClock(TenantId tenant, int offsetMs, DateTimeOffset measuredAt);

    /// <summary>
    /// Writes what was added and commits, with the handlers' work; false (nothing kept) if a row was stored by an upload
    /// that did not take the tablet's lock, which should never happen.
    /// </summary>
    Task<bool> CommitAsync(CancellationToken cancellationToken);
}

public interface IInboxStore
{
    /// <summary>Starts the upload's transaction, takes the tablet's lock and reads what the batch is decided against.</summary>
    Task<IInboxSession> OpenAsync(Guid deviceId, IReadOnlyCollection<CommandId> ids, CancellationToken cancellationToken);
}

/// <summary>The bytes a tablet signs for a command: RFC 8785 of <c>{commandId, deviceTime, payload, sequence, type}</c>.</summary>
public static class CommandBody
{
    /// <summary>The canonical text, or null when the payload is not an object or holds a number a double cannot carry exactly.</summary>
    public static string? Canonical(UploadedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var unsigned = new JsonObject
        {
            ["commandId"] = command.CommandId,
            ["sequence"] = command.Sequence,
            ["type"] = command.Type,
            ["payload"] = JsonNode.Parse(command.Payload.GetRawText()),
            ["deviceTime"] = command.DeviceTime,
        };
        try
        {
            return Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(unsigned.ToJsonString()));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed class UploadBatchValidator : AbstractValidator<UploadBatch>
{
    public UploadBatchValidator()
    {
        RuleFor(b => b.Commands).Must(c => c is { Count: >= 1 and <= InboxRules.MaxCommands })
            .WithMessage($"Send 1 to {InboxRules.MaxCommands} commands.");
        RuleForEach(b => b.Commands).NotNull().ChildRules(command =>
        {
            command.RuleFor(c => c.CommandId).Must(id => InboxRules.TryParseCommandId(id, out _)).WithMessage("A lowercase UUIDv7 is required.");
            command.RuleFor(c => c.Sequence).GreaterThanOrEqualTo(1);
            command.RuleFor(c => c.Type).Must(InboxRules.IsCommandType).WithMessage("A versioned command type is required, e.g. production.trip_recorded.v1.");
            command.RuleFor(c => c.DeviceTime).Must(t => InboxRules.TryParseDeviceTime(t, out _)).WithMessage("An ISO 8601 time with an offset is required.");
            command.RuleFor(c => c.Signature).NotEmpty().MaximumLength(100);
            command.RuleFor(c => c.Payload).Must((c, _) => CommandBody.Canonical(c) is { } body && Encoding.UTF8.GetByteCount(body) <= InboxRules.MaxBodyBytes)
                .WithMessage($"The payload must be a JSON object of at most {InboxRules.MaxBodyBytes / 1024} KB whose numbers are exact (amounts as strings).");
        });
    }
}

/// <summary>
/// Uploads from tablets (ADR-0028 §6, ADR-0031): each command is checked against the device's key and its place in
/// the device's sequence, handled by the module that owns its type, and stored in the inbox, all in one transaction;
/// the answer gives each command's final status. A revoked device's commands are stored quarantined, never handled,
/// and the device is told it is revoked (§3).
/// </summary>
public sealed partial class SyncUploads(
    IInboxStore store, IDeviceDirectory devices, SyncCommandTypes handlers, IServiceScopeFactory scopes, ITenantContext tenant,
    IEvidenceStore evidenceStore, TimeProvider clock, ILogger<SyncUploads> logger)
{
    public static readonly Error Revoked = Error.Forbidden(PermissionMiddleware.RevokedDeviceCode, "This tablet has been revoked.");

    public static readonly Error BatchConflict = Error.Conflict("sync.batch_conflict", "Another upload of this tablet was stored at the same time; send the batch again.");

    public async Task<Result<BatchReceipt>> UploadAsync(DeviceCaller caller, UploadBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var tenantId = tenant.Current ?? throw new InvalidOperationException("A device request always runs for its tenant.");
        var now = clock.GetUtcNow();
        var uploaded = batch.Commands!;
        var commands = uploaded.Select(c => Signed(c)).ToList();
        await using var session = await store.OpenAsync(caller.DeviceId, [.. commands.Select(c => c.Id)], cancellationToken).ConfigureAwait(false);

        if (caller.Revoked)
        {
            var kept = commands.Where(c => !session.Stored.Contains(c.Id)).DistinctBy(c => c.Id).ToList();
            kept.ForEach(c => session.Add(InboxCommand.Quarantine(tenantId, caller.DeviceId, c, now)));
            if (!await session.CommitAsync(cancellationToken).ConfigureAwait(false))
            {
                return BatchConflict;
            }

            LogQuarantined(logger, caller.DeviceId, kept.Count);
            return Revoked;
        }

        var key = await devices.FindAsync(caller.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An authenticated device has a key.");
        // The tablet's clock against ours: the time it signed this request (whole seconds, so within a second plus the
        // network time). Assertions are refused beyond 5 minutes of skew, so the offset stays within that.
        var offset = caller.IssuedAt is { } signedAt ? DeviceClock.OffsetOf(signedAt, now) : 0;
        session.MeasureClock(tenantId, offset, now);
        var keptUnhandled = new HashSet<CommandId>(session.Unhandled);
        var earlier = session.Waiting.ToDictionary(w => w.Id);
        var plan = await InboxRules.DecideAsync(
            session.LastSequence, session.Stored, [.. earlier.Values.Select(w => new UploadCandidate(w.Id, w.Sequence, SignatureValid: true))],
            commands.Select(c => new UploadCandidate(c.Id, c.Sequence, Verify(key, c))),
            c => c.Index >= 0
                ? HandleAsync(session, caller, tenantId, new Arrival(commands[c.Index], uploaded[c.Index].Payload, now, offset), now, keptUnhandled, cancellationToken)
                : HandleAsync(session, caller, tenantId, Arrival.Of(earlier[c.Id]), now, keptUnhandled, cancellationToken))
            .ConfigureAwait(false);
        foreach (var held in plan.Held)
        {
            session.Add(new WaitingCommand(tenantId, caller.DeviceId, commands[held.Index], now, offset));
        }

        if (!await session.CommitAsync(cancellationToken).ConfigureAwait(false))
        {
            return BatchConflict;
        }

        var decisions = plan.Decisions;
        if (plan.Promoted.Count > 0)
        {
            var handled = plan.Promoted.Count(d => d.Stored);
            LogPromoted(logger, caller.DeviceId, handled, plan.Promoted.Count);
        }

        var invalid = decisions.Count(d => d.Code == InboxRules.SignatureInvalid);
        if (invalid > 0)
        {
            LogBadSignatures(logger, caller.DeviceId, invalid);
        }

        return new BatchReceipt(
            [.. decisions.Select(d => new CommandReceipt(d.Id.Value, d.Outcome.ToString().ToLowerInvariant(), d.Code, d.Retryable, d.Flags ?? []))],
            plan.LastSequence, plan.MissingSequences, clock.GetUtcNow());
    }

    /// <summary>
    /// Hands a command that is next in the device's sequence to its type's handler, inside a savepoint: if the handler
    /// throws or rejects, nothing of the command remains and the batch goes on (ADR-0031 §2–§3).
    /// </summary>
    private async Task<UploadDecision> HandleAsync(
        IInboxSession session, DeviceCaller caller, TenantId tenantId, Arrival arrival, DateTimeOffset now,
        HashSet<CommandId> keptUnhandled, CancellationToken cancellationToken)
    {
        var command = arrival.Command;
        if (handlers.HandlerOf(command.Type) is not { } handlerType)
        {
            if (keptUnhandled.Add(command.Id))
            {
                session.Add(new UnhandledCommand(tenantId, caller.DeviceId, command, InboxRules.TypeUnknown, arrival.ReceivedAt));
            }

            LogTypeUnknown(logger, caller.DeviceId, command.Type);
            return UploadDecision.Rejected(command.Id, InboxRules.TypeUnknown, retryable: true);
        }

        var (evidence, refused) = await CheckEvidenceAsync(tenantId, caller.DeviceId, command, arrival.Payload, cancellationToken).ConfigureAwait(false);
        if (refused is { } refusal)
        {
            return refusal.Retryable ? UploadDecision.Rejected(command.Id, refusal.Code, retryable: true) : KeepRejected(refusal.Code);
        }

        var verified = new VerifiedCommand(command.Id.Value, caller.DeviceId, caller.SiteId, command.Sequence, command.Type,
            arrival.Payload.Clone(), command.DeviceTime, arrival.ReceivedAt, arrival.ClockOffsetMs, command.Body);
        var savepoint = $"command_{command.Sequence}";
        await session.SavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
        HandlerOutcome outcome;
        try
        {
            // A scope per command: a handler that failed leaves nothing tracked behind for the next one.
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
            var handler = (ISyncCommandHandler)scope.ServiceProvider.GetRequiredService(handlerType);
            if (handler.Type != command.Type)
            {
                throw new InvalidOperationException($"{handlerType.FullName} is registered for {command.Type} but handles {handler.Type}.");
            }

            outcome = await handler.HandleAsync(verified, session.Transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await session.RollbackToAsync(savepoint, cancellationToken).ConfigureAwait(false);
            LogHandlerFailed(logger, ex, caller.DeviceId, command.Type, command.Id.Value);
            return UploadDecision.Rejected(command.Id, InboxRules.HandlerFailed, retryable: true);
        }

        switch (outcome)
        {
            case HandlerOutcome.Accepted accepted:
                await session.ReleaseAsync(savepoint, cancellationToken).ConfigureAwait(false);
                Store(UploadOutcome.Accepted, accepted.RecordId, []);
                return UploadDecision.Accepted(command.Id);
            case HandlerOutcome.Conflict conflict:
                await session.ReleaseAsync(savepoint, cancellationToken).ConfigureAwait(false);
                Store(UploadOutcome.Conflict, conflict.RecordId, conflict.Flags);
                return new UploadDecision(command.Id, UploadOutcome.Conflict, null, Flags: conflict.Flags);
            case HandlerOutcome.Rejected rejected:
                await session.RollbackToAsync(savepoint, cancellationToken).ConfigureAwait(false);
                return rejected.Retryable ? UploadDecision.Rejected(command.Id, rejected.Code, retryable: true) : KeepRejected(rejected.Code);
            default:
                throw new InvalidOperationException($"Unknown handler outcome {outcome}.");
        }

        // A final rejection takes its place in the sequence, kept with its code, so the tablet's later commands are
        // not blocked behind a command it can never change (ADR-0031 §3). Nothing of it was handled.
        UploadDecision KeepRejected(string code)
        {
            session.Add(InboxCommand.Receive(tenantId, caller.DeviceId, command, arrival.ReceivedAt));
            session.Add(ProcessedCommand.Rejected(tenantId, caller.DeviceId, command.Id, code, now));
            LogKeptRejected(logger, caller.DeviceId, command.Id.Value, code);
            return UploadDecision.RejectedAndKept(command.Id, code);
        }

        void Store(UploadOutcome stored, Guid? recordId, IReadOnlyList<string> flags)
        {
            session.Add(InboxCommand.Receive(tenantId, caller.DeviceId, command, arrival.ReceivedAt));
            session.Add(new ProcessedCommand(tenantId, caller.DeviceId, command.Id, stored, recordId, flags, now));
            foreach (var blob in evidence)
            {
                session.Add(new CommandEvidence(tenantId, caller.DeviceId, command.Id, blob.Sha256, blob.Size, blob.ContentType, now));
            }
        }
    }

    /// <summary>
    /// The blobs the command's signed payload names must be in the store, under this tablet's keys, exactly as named
    /// (ADR-0032 §2): a missing one is retryable (the tablet uploads it and sends again); other content under the key
    /// (only possible by writing to the store around the API) is refused for good and logged as a security event.
    /// </summary>
    private async Task<(IReadOnlyList<EvidenceRef> Evidence, (string Code, bool Retryable)? Refused)> CheckEvidenceAsync(
        TenantId tenantId, Guid deviceId, SignedCommand command, JsonElement payload, CancellationToken cancellationToken)
    {
        if (CommandEvidenceRefs.Read(payload) is not { } evidence)
        {
            return ([], (SyncBlobRules.EvidenceInvalid, false));
        }

        if (evidence.Count == 0)
        {
            return (evidence, null);
        }

        if (!evidenceStore.Available)
        {
            return (evidence, (SyncBlobRules.UnavailableCode, true));
        }

        foreach (var blob in evidence)
        {
            BlobHead? head;
            try
            {
                head = await evidenceStore.HeadAsync(EvidenceKeys.For(tenantId.Value, deviceId, blob.Sha256), cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectStoreUnavailableException)
            {
                return (evidence, (SyncBlobRules.UnavailableCode, true));
            }

            if (head is null)
            {
                return (evidence, (SyncBlobRules.BlobMissing, true));
            }

            if (head.Size != blob.Size || head.Sha256 != SyncBlobRules.ToBase64(blob.Sha256) || head.ContentType != blob.ContentType)
            {
                LogBlobMismatch(logger, deviceId, command.Id.Value, blob.Sha256);
                return (evidence, (SyncBlobRules.BlobMismatch, false));
            }
        }

        return (evidence, null);
    }

    /// <summary>
    /// A command as it came: now, or earlier for one that waited for an earlier number, which keeps the time and clock
    /// offset of its arrival (ADR-0031 §4, §6).
    /// </summary>
    private sealed record Arrival(SignedCommand Command, JsonElement Payload, DateTimeOffset ReceivedAt, int ClockOffsetMs)
    {
        public static Arrival Of(WaitingCommand waiting) => new(waiting.Command, PayloadOf(waiting.Body), waiting.ReceivedAt, waiting.ClockOffsetMs);
    }

    /// <summary>The payload of a stored command, from the text the tablet signed.</summary>
    private static JsonElement PayloadOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("payload").Clone();
    }

    /// <summary>The command as stored; the validator has already checked every part.</summary>
    private static SignedCommand Signed(UploadedCommand command) =>
        InboxRules.TryParseCommandId(command.CommandId, out var id) && InboxRules.TryParseDeviceTime(command.DeviceTime, out var deviceTime)
            ? new SignedCommand(id, command.Sequence, command.Type!, deviceTime, CommandBody.Canonical(command)!, command.Signature!)
            : throw new InvalidOperationException("The batch was not validated.");

    private static bool Verify(DeviceKey key, SignedCommand command)
    {
        Span<byte> signature = stackalloc byte[75];
        if (Base64Url.DecodeFromChars(command.Signature, signature, out var consumed, out var written) != System.Buffers.OperationStatus.Done
            || consumed != command.Signature.Length || written != 64)
        {
            return false;
        }

        using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = key.X, Y = key.Y } });
        return ecdsa.VerifyData(Encoding.UTF8.GetBytes(command.Body), signature[..64], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    [LoggerMessage(EventId = 7209, Level = LogLevel.Warning, Message = "Command {CommandId} of tablet {DeviceId} was rejected for good ({Code}) and kept in its sequence")]
    private static partial void LogKeptRejected(ILogger logger, Guid deviceId, Guid commandId, string code);

    [LoggerMessage(EventId = 7208, Level = LogLevel.Warning,
        Message = "Security: command {CommandId} of tablet {DeviceId} names blob {Sha256}, but the store holds other content under its key")]
    private static partial void LogBlobMismatch(ILogger logger, Guid deviceId, Guid commandId, string sha256);

    [LoggerMessage(EventId = 7201, Level = LogLevel.Warning, Message = "Revoked device {DeviceId} uploaded commands; {Count} new one(s) quarantined")]
    private static partial void LogQuarantined(ILogger logger, Guid deviceId, int count);

    [LoggerMessage(EventId = 7202, Level = LogLevel.Warning, Message = "Device {DeviceId} uploaded {Count} command(s) whose signature does not verify")]
    private static partial void LogBadSignatures(ILogger logger, Guid deviceId, int count);

    [LoggerMessage(EventId = 7205, Level = LogLevel.Information, Message = "Device {DeviceId}: {Stored} of {Count} waiting command(s) handled after their gap closed")]
    private static partial void LogPromoted(ILogger logger, Guid deviceId, int stored, int count);

    [LoggerMessage(EventId = 7203, Level = LogLevel.Warning, Message = "Device {DeviceId} sent a command of type {Type}, which no module handles; kept unhandled")]
    private static partial void LogTypeUnknown(ILogger logger, Guid deviceId, string type);

    [LoggerMessage(EventId = 7204, Level = LogLevel.Error, Message = "The handler of {Type} failed on command {CommandId} of device {DeviceId}; the tablet will send it again")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, Guid deviceId, string type, Guid commandId);
}
