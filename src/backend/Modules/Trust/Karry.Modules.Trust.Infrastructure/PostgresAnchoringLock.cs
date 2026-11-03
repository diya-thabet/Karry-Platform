using Karry.Modules.Trust.Application;
using Npgsql;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>
/// A PostgreSQL advisory lock on the application database, which every Workers process and replica shares (hardening
/// plan H-C5). It is held by a session of its own for the length of the run; ending the session (a crash included)
/// releases it. A waiting run blocks until the first finishes, so both report a completed run.
/// </summary>
internal sealed class PostgresAnchoringLock(NpgsqlDataSource database) : IAnchoringLock
{
    /// <summary>Names this lock among the database's advisory locks ("KARRYANC").</summary>
    internal const long Key = 0x4B415252_59414E43;

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", Key);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new Held(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Held(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Released explicitly: the connection goes back to the pool, where the session lives on.
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                command.Parameters.AddWithValue("key", Key);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
