using System.Data.Common;
using Karry.BuildingBlocks.Ledger;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Domain;
using Karry.SharedKernel.Tenancy;
using Npgsql;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>
/// Appends to <c>audit.ledger_entries</c> in the caller's transaction (US-P1-AUD-01). Per tenant: create the head row
/// if missing, lock it FOR UPDATE (writers of the same tenant queue here: gap-free sequence under concurrency),
/// chain the entries, update the head. Tenants are processed in a fixed order so two transactions never deadlock.
/// </summary>
internal sealed class PostgresLedgerWriter : ILedgerWriter
{
    public async Task AppendAsync(DbConnection connection, DbTransaction transaction, TenantId? unitOfWorkTenant,
        IReadOnlyList<LedgerRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var group in records.GroupBy(r => r.Tenant).OrderBy(g => g.Key.Value))
        {
            if (unitOfWorkTenant is { } current && current != group.Key)
            {
                throw new InvalidOperationException("A unit of work cannot write another tenant's ledger.");
            }

            // RLS on the ledger: set the entry's tenant for the rest of this transaction (no-op when it is the unit
            // of work's tenant; required for platform work such as provisioning, which has none).
            await ExecuteAsync(connection, transaction, TenantCommandInterceptor.SetLocalStatement(group.Key.Value), cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO audit.ledger_heads (tenant_id, last_seq, last_hash) VALUES (@tenant, 0, @genesis) ON CONFLICT (tenant_id) DO NOTHING",
                cancellationToken, ("tenant", group.Key.Value), ("genesis", LedgerHash.Genesis)).ConfigureAwait(false);

            long sequence;
            string previous;
            await using (var head = Command(connection, transaction,
                "SELECT last_seq, last_hash FROM audit.ledger_heads WHERE tenant_id = @tenant FOR UPDATE", ("tenant", group.Key.Value)))
            await using (var reader = await head.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Ledger head not visible after creation (row-level security misconfigured?).");
                }

                sequence = reader.GetInt64(0);
                previous = reader.GetString(1);
            }

            foreach (var record in group)
            {
                sequence++;
                var payload = LedgerHash.CanonicalPayload(record.Payload);
                var payloadHash = LedgerHash.OfPayload(payload);
                var occurredAt = LedgerHash.ToStoragePrecision(record.OccurredAt);
                var entryHash = LedgerHash.OfEntry(previous, group.Key, sequence, record.EventType, payloadHash, occurredAt);
                await ExecuteAsync(connection, transaction,
                    """
                    INSERT INTO audit.ledger_entries (tenant_id, seq, event_id, event_type, occurred_at, payload, payload_hash, prev_hash, entry_hash)
                    VALUES (@tenant, @seq, @event_id, @type, @occurred_at, @payload, @payload_hash, @prev_hash, @entry_hash)
                    """,
                    cancellationToken,
                    ("tenant", group.Key.Value), ("seq", sequence), ("event_id", record.EventId), ("type", record.EventType),
                    ("occurred_at", occurredAt), ("payload", payload), ("payload_hash", payloadHash), ("prev_hash", previous),
                    ("entry_hash", entryHash)).ConfigureAwait(false);
                if (record.Subject is { } subject)
                {
                    // What the entry is about, for the audit viewer's filters (ADR-0029 §1): derived, not hashed. An
                    // event without a site of its own takes its entity's latest known site (an asset's status change
                    // belongs to the asset's site), so site-scoped readers see it.
                    await ExecuteAsync(connection, transaction,
                        """
                        INSERT INTO audit.ledger_subjects (tenant_id, seq, actor, entity_type, entity_id, site_id)
                        VALUES (@tenant, @seq, @actor, @entity_type, @entity_id, COALESCE(@site_id, (
                          SELECT s.site_id FROM audit.ledger_subjects s
                          WHERE s.tenant_id = @tenant AND s.entity_type = @entity_type AND s.entity_id = @entity_id AND s.site_id IS NOT NULL
                          ORDER BY s.seq DESC LIMIT 1)))
                        """,
                        cancellationToken,
                        ("tenant", group.Key.Value), ("seq", sequence), ("actor", (object?)subject.Actor ?? DBNull.Value),
                        ("entity_type", subject.Entity), ("entity_id", (object?)subject.EntityId ?? DBNull.Value),
                        ("site_id", (object?)subject.SiteId ?? DBNull.Value)).ConfigureAwait(false);
                }

                previous = entryHash;
            }

            await ExecuteAsync(connection, transaction,
                "UPDATE audit.ledger_heads SET last_seq = @seq, last_hash = @hash WHERE tenant_id = @tenant",
                cancellationToken, ("tenant", group.Key.Value), ("seq", sequence), ("hash", previous)).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Only constant SQL from this class reaches here; all values are bound parameters (the SET LOCAL value is a
        // .NET-formatted Guid, see TenantCommandInterceptor).
        command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value));
        }

        return command;
    }
}
