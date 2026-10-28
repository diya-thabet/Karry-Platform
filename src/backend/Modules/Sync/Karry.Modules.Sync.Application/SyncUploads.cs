using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Sync.Domain;
using Karry.SharedKernel.Canonical;
using Karry.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Sync.Application;

/// <summary>
/// A command as the tablet's outbox holds it (ADR-0028 §6, <c>@karry/offline</c> <c>OutboxRecord</c>):
/// <paramref name="Signature"/> is ES256 (IEEE P1363, base64url) over the RFC 8785 form of the other five members.
/// </summary>
public sealed record UploadedCommand(string? CommandId, long Sequence, string? Type, JsonElement Payload, string? DeviceTime, string? Signature);

public sealed record UploadBatch(IReadOnlyList<UploadedCommand>? Commands);

/// <summary>What happened to one command: <c>accepted</c>, <c>duplicate</c> or <c>rejected</c> (with <see cref="Code"/>).</summary>
public sealed record CommandReceipt(Guid CommandId, string Status, string? Code);

/// <summary>The answer to a batch, in its order; <see cref="LastSequence"/> is the device's last stored sequence number.</summary>
public sealed record BatchReceipt(IReadOnlyList<CommandReceipt> Results, long LastSequence);

public interface IInboxStore
{
    /// <summary>The highest sequence number among the device's received commands; 0 before its first.</summary>
    Task<long> LastSequenceAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="ids"/> the device has already uploaded (received or quarantined).</summary>
    Task<IReadOnlySet<CommandId>> StoredAsync(Guid deviceId, IReadOnlyCollection<CommandId> ids, CancellationToken cancellationToken);

    /// <summary>Stores the commands in one transaction; false when another upload of the device stored one of them first.</summary>
    Task<bool> TryAddAsync(IReadOnlyCollection<InboxCommand> commands, CancellationToken cancellationToken);
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
/// Uploads from tablets (ADR-0028 §6): each command is checked against the device's key and stored in the inbox in
/// its sequence, or reported as a duplicate or rejected. P1 stores commands without processing them. A revoked
/// device's commands are stored quarantined, never processed, and the device is told it is revoked (§3).
/// </summary>
public sealed partial class SyncUploads(IInboxStore store, IDeviceDirectory devices, ITenantContext tenant, TimeProvider clock, ILogger<SyncUploads> logger)
{
    public static readonly Error Revoked = Error.Forbidden(PermissionMiddleware.RevokedDeviceCode, "This tablet has been revoked.");

    public static readonly Error BatchConflict = Error.Conflict("sync.batch_conflict", "Another upload of this tablet was stored at the same time; send the batch again.");

    public async Task<Result<BatchReceipt>> UploadAsync(DeviceCaller caller, UploadBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var tenantId = tenant.Current ?? throw new InvalidOperationException("A device request always runs for its tenant.");
        var now = clock.GetUtcNow();
        var commands = batch.Commands!.Select(c => Signed(c)).ToList();
        var stored = await store.StoredAsync(caller.DeviceId, [.. commands.Select(c => c.Id)], cancellationToken).ConfigureAwait(false);

        if (caller.Revoked)
        {
            var kept = commands.Where(c => !stored.Contains(c.Id)).DistinctBy(c => c.Id).Select(c => InboxCommand.Quarantine(tenantId, caller.DeviceId, c, now)).ToList();
            if (kept.Count > 0 && !await store.TryAddAsync(kept, cancellationToken).ConfigureAwait(false))
            {
                return BatchConflict;
            }

            LogQuarantined(logger, caller.DeviceId, kept.Count);
            return Revoked;
        }

        var key = await devices.FindAsync(caller.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An authenticated device has a key.");
        var last = await store.LastSequenceAsync(caller.DeviceId, cancellationToken).ConfigureAwait(false);
        var (decisions, lastSequence) = InboxRules.Decide(last, stored, commands.Select(c => new UploadCandidate(c.Id, c.Sequence, Verify(key, c))));

        var received = commands.Where((_, i) => decisions[i].Outcome == UploadOutcome.Accepted)
            .Select(c => InboxCommand.Receive(tenantId, caller.DeviceId, c, now)).ToList();
        if (received.Count > 0 && !await store.TryAddAsync(received, cancellationToken).ConfigureAwait(false))
        {
            return BatchConflict;
        }

        var invalid = decisions.Count(d => d.Code == InboxRules.SignatureInvalid);
        if (invalid > 0)
        {
            LogBadSignatures(logger, caller.DeviceId, invalid);
        }

        return new BatchReceipt([.. decisions.Select(d => new CommandReceipt(d.Id.Value, d.Outcome.ToString().ToLowerInvariant(), d.Code))], lastSequence);
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
}
