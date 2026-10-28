using Npgsql;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>
/// Serializes changes to a shared Keycloak client (the portal's redirect URIs and web origins) between processes.
/// Keycloak's admin API replaces a client whole and has no version check, so two read-modify-writes at the same time
/// lose one company's address and its people can no longer sign in.
/// </summary>
internal interface IKeycloakClientLock
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A PostgreSQL advisory lock on the application database, which the API host, the workers and every replica share. It
/// is held by a session of its own for the length of the change; ending the session releases it, even on a crash.
/// </summary>
internal sealed class PostgresKeycloakClientLock(string connectionString) : IKeycloakClientLock
{
    /// <summary>Names this lock among the database's advisory locks ("KARRYKC").</summary>
    internal const long Key = 0x4B4152_52594B43;

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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
