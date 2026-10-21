using Npgsql;

namespace Karry.Spikes.RlsPooling.Tests;

internal static class Db
{
    public static async Task InsertEntryAsync(TenantScope scope, Guid tenantId, decimal meterValue, CancellationToken ct, string table = "spike.shift_entry")
    {
        await using var command = scope.CreateCommand($"INSERT INTO {table} (id, tenant_id, meter_value) VALUES ($1, $2, $3)");
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(meterValue);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<List<Guid>> ReadTenantIdsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var tenants = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tenants.Add(reader.GetGuid(0));
        }

        return tenants;
    }

    public static async Task<List<Guid>> VisibleTenantIdsAsync(TenantScope scope, CancellationToken ct, string table = "spike.shift_entry")
    {
        await using var command = scope.CreateCommand($"SELECT tenant_id FROM {table}");
        return await ReadTenantIdsAsync(command, ct);
    }

    /// <summary>Reads rows on a raw connection with NO tenant scope (what a buggy code path would do).</summary>
    public static async Task<List<Guid>> UnscopedVisibleTenantIdsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT tenant_id FROM spike.shift_entry", connection);
        return await ReadTenantIdsAsync(command, ct);
    }

    public static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var p in parameters)
        {
            command.Parameters.AddWithValue(p);
        }

        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
