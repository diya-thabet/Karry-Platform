using System.Data.Common;
using System.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Ledger;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>
/// <see cref="IEnvelopeRecorder"/> on <c>audit.signature_envelopes</c> (append-only, RLS), in the command's
/// transaction: the envelope's canonical text is kept exactly as signed, and the ledger entry
/// <see cref="EnvelopeLedger.ReceivedEvent"/> binds its hash to the server's receipt time (ADR-0033 §4).
/// </summary>
internal sealed partial class PostgresEnvelopeRecorder(IDeviceDirectory devices, ILedgerWriter ledger, ILogger<PostgresEnvelopeRecorder> logger) : IEnvelopeRecorder
{
    public async Task<EnvelopeOutcome> RecordAsync(DbConnection connection, DbTransaction transaction, EnvelopeSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var envelope = submission.Envelope;
        if (SignatureEnvelopes.Problem(envelope) is not null)
        {
            return new EnvelopeOutcome.Refused(EnvelopeCodes.Invalid);
        }

        if (envelope.GetProperty("signer").GetProperty("deviceId").GetString() != submission.DeviceId.ToString())
        {
            return new EnvelopeOutcome.Refused(EnvelopeCodes.DeviceMismatch);
        }

        // The key of the device that sent the command (its signature was checked moments ago with the same key).
        if (await devices.FindAsync(submission.DeviceId, cancellationToken).ConfigureAwait(false) is not { } key
            || !SignatureEnvelopes.VerifyDevice(envelope, key.X, key.Y, submission.DeviceSignature))
        {
            LogSignatureInvalid(logger, submission.TenantId, submission.DeviceId, submission.CommandId);
            return new EnvelopeOutcome.Refused(EnvelopeCodes.SignatureInvalid);
        }

        var hash = SignatureEnvelopes.Hash(envelope);
        var envelopeId = Guid.Parse(envelope.GetProperty("envelopeId").GetString()!);
        var tenant = new TenantId(submission.TenantId);
        await ExecuteAsync(connection, transaction, TenantCommandInterceptor.SetLocalStatement(tenant.Value), cancellationToken).ConfigureAwait(false);

        await using (var existing = Command(connection, transaction, "SELECT envelope_hash FROM audit.signature_envelopes WHERE envelope_id = @id", ("id", envelopeId)))
        {
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string stored)
            {
                if (stored == hash)
                {
                    return new EnvelopeOutcome.AlreadyRecorded(hash);
                }

                LogConflict(logger, submission.TenantId, submission.DeviceId, envelopeId);
                return new EnvelopeOutcome.Refused(EnvelopeCodes.Conflict);
            }
        }

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO audit.signature_envelopes (envelope_id, tenant_id, record_type, record_id, record_version, envelope_hash,
              canonical, device_id, device_signature, command_id, received_at)
            VALUES (@id, @tenant, @record_type, @record_id, @record_version, @hash, @canonical, @device, @signature, @command, @received_at)
            """,
            cancellationToken,
            ("id", envelopeId), ("tenant", tenant.Value), ("record_type", envelope.GetProperty("recordType").GetString()!),
            ("record_id", Guid.Parse(envelope.GetProperty("recordId").GetString()!)), ("record_version", envelope.GetProperty("recordVersion").GetInt32()),
            ("hash", hash), ("canonical", Encoding.UTF8.GetString(SignatureEnvelopes.Canonical(envelope))), ("device", submission.DeviceId),
            ("signature", submission.DeviceSignature), ("command", submission.CommandId), ("received_at", submission.ReceivedAt)).ConfigureAwait(false);

        var received = JsonSerializer.Serialize(new
        {
            envelopeId,
            envelopeHash = hash,
            recordType = envelope.GetProperty("recordType").GetString(),
            recordId = envelope.GetProperty("recordId").GetString(),
            recordVersion = envelope.GetProperty("recordVersion").GetInt32(),
            deviceId = submission.DeviceId,
            commandId = submission.CommandId,
            receivedAt = submission.ReceivedAt,
        });
        await ledger.AppendAsync(connection, transaction, tenant,
            [new LedgerRecord(tenant, Guid.CreateVersion7(submission.ReceivedAt), EnvelopeLedger.ReceivedEvent, received, submission.ReceivedAt)], cancellationToken).ConfigureAwait(false);
        return new EnvelopeOutcome.Recorded(hash);
    }

    private static NpgsqlCommand Command(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 4010, Level = LogLevel.Warning,
        Message = "Security: the signature envelope of command {CommandId} from tablet {DeviceId} of tenant {Tenant} does not verify; refused")]
    private static partial void LogSignatureInvalid(ILogger logger, Guid tenant, Guid deviceId, Guid commandId);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Warning,
        Message = "Security: tablet {DeviceId} of tenant {Tenant} sent envelope {EnvelopeId} again with other content; refused")]
    private static partial void LogConflict(ILogger logger, Guid tenant, Guid deviceId, Guid envelopeId);
}
