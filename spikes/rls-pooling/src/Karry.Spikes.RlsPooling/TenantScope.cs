using Npgsql;

namespace Karry.Spikes.RlsPooling;

/// <summary>
/// A database unit of work bound to exactly one tenant.
/// Opens a pooled connection, starts a transaction and sets <c>app.tenant_id</c>
/// with <c>set_config(..., is_local =&gt; true)</c>, the parameterizable equivalent of
/// <c>SET LOCAL</c>. The setting dies with the transaction, so a pooled connection
/// can never carry one tenant's context into another tenant's request.
/// </summary>
public sealed class TenantScope : IAsyncDisposable
{
    private bool _completed;

    private TenantScope(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId)
    {
        Connection = connection;
        Transaction = transaction;
        TenantId = tenantId;
    }

    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }
    public Guid TenantId { get; }

    public static async Task<TenantScope> BeginAsync(NpgsqlDataSource dataSource, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant scope requires a non-empty tenant id.", nameof(tenantId));
        }

        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection, transaction);
            command.Parameters.AddWithValue(tenantId.ToString());
            await command.ExecuteScalarAsync(cancellationToken);
            return new TenantScope(connection, transaction, tenantId);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public NpgsqlCommand CreateCommand(string sql) => new(sql, Connection, Transaction);

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await Transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await Transaction.RollbackAsync();
        }

        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
