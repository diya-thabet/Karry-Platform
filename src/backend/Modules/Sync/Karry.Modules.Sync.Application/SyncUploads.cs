using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Devices;
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
/// <c>duplicate</c> or <c>rejected</c> (with <see cref="Code"/>; <see cref="Retryable"/> when sending it again later
/// can succeed). Every status but <c>rejected</c> is final: the tablet marks the command sent.
/// </summary>
public sealed record CommandReceipt(Guid CommandId, string Status, string? Code, bool Retryable, IReadOnlyList<string> Flags);

/// <summary>
/// The answer to a batch, in its order; <see cref="LastSequence"/> is the device's last stored sequence number and
/// <see cref="ServerTime"/> the server's clock when it answered (the tablet measures its offset from it, ADR-0031 §6).
/// </summary>
public sealed record BatchReceipt(IReadOnlyList<CommandReceipt> Results, long LastSequence, DateTimeOffset ServerTime);

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

    /// <summary>The transaction command handlers join.</summary>
    ISyncTransaction Transaction { get; }

    Task SavepointAsync(string name, CancellationToken cancellationToken);

    Task ReleaseAsync(string name, CancellationToken cancellationToken);

    /// <summary>Undoes everything done since the savepoint, also after a failed statement.</summary>
    Task RollbackToAsync(string name, CancellationToken cancellationToken);

    void Add(InboxCommand command);

    void Add(ProcessedCommand processed);

    void Add(UnhandledCommand unhandled);

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
    TimeProvider clock, ILogger<SyncUploads> logger)
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
        var keptUnhandled = new HashSet<CommandId>(session.Unhandled);
        var (decisions, lastSequence) = await InboxRules.DecideAsync(
            session.LastSequence, session.Stored, commands.Select(c => new UploadCandidate(c.Id, c.Sequence, Verify(key, c))),
            (_, i) => HandleAsync(session, caller, tenantId, commands[i], uploaded[i], now, keptUnhandled, cancellationToken)).ConfigureAwait(false);
        if (!await session.CommitAsync(cancellationToken).ConfigureAwait(false))
        {
            return BatchConflict;
        }

        var invalid = decisions.Count(d => d.Code == InboxRules.SignatureInvalid);
        if (invalid > 0)
        {
            LogBadSignatures(logger, caller.DeviceId, invalid);
        }

        return new BatchReceipt(
            [.. decisions.Select(d => new CommandReceipt(d.Id.Value, d.Outcome.ToString().ToLowerInvariant(), d.Code, d.Retryable, d.Flags ?? []))],
            lastSequence, clock.GetUtcNow());
    }

    /// <summary>
    /// Hands a command that is next in the device's sequence to its type's handler, inside a savepoint: if the handler
    /// throws or rejects, nothing of the command remains and the batch goes on (ADR-0031 §2–§3).
    /// </summary>
    private async Task<UploadDecision> HandleAsync(
        IInboxSession session, DeviceCaller caller, TenantId tenantId, SignedCommand command, UploadedCommand uploaded, DateTimeOffset now,
        HashSet<CommandId> keptUnhandled, CancellationToken cancellationToken)
    {
        if (handlers.HandlerOf(command.Type) is not { } handlerType)
        {
            if (keptUnhandled.Add(command.Id))
            {
                session.Add(new UnhandledCommand(tenantId, caller.DeviceId, command, InboxRules.TypeUnknown, now));
            }

            LogTypeUnknown(logger, caller.DeviceId, command.Type);
            return UploadDecision.Rejected(command.Id, InboxRules.TypeUnknown, retryable: true);
        }

        var verified = new VerifiedCommand(command.Id.Value, caller.DeviceId, caller.SiteId, command.Sequence, command.Type,
            uploaded.Payload.Clone(), command.DeviceTime, now, ClockOffsetMs: 0, command.Body);
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
                return UploadDecision.Rejected(command.Id, rejected.Code, rejected.Retryable);
            default:
                throw new InvalidOperationException($"Unknown handler outcome {outcome}.");
        }

        void Store(UploadOutcome stored, Guid? recordId, IReadOnlyList<string> flags)
        {
            session.Add(InboxCommand.Receive(tenantId, caller.DeviceId, command, now));
            session.Add(new ProcessedCommand(tenantId, caller.DeviceId, command.Id, stored, recordId, flags, now));
        }
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

    [LoggerMessage(EventId = 7201, Level = LogLevel.Warning, Message = "Revoked device {DeviceId} uploaded commands; {Count} new one(s) quarantined")]
    private static partial void LogQuarantined(ILogger logger, Guid deviceId, int count);

    [LoggerMessage(EventId = 7202, Level = LogLevel.Warning, Message = "Device {DeviceId} uploaded {Count} command(s) whose signature does not verify")]
    private static partial void LogBadSignatures(ILogger logger, Guid deviceId, int count);

    [LoggerMessage(EventId = 7203, Level = LogLevel.Warning, Message = "Device {DeviceId} sent a command of type {Type}, which no module handles; kept unhandled")]
    private static partial void LogTypeUnknown(ILogger logger, Guid deviceId, string type);

    [LoggerMessage(EventId = 7204, Level = LogLevel.Error, Message = "The handler of {Type} failed on command {CommandId} of device {DeviceId}; the tablet will send it again")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, Guid deviceId, string type, Guid commandId);
}
