using Karry.Spikes.RlsPooling;
using Npgsql;

[assembly: AssemblyFixture(typeof(Karry.Spikes.RlsPooling.Tests.PostgresFixture))]

namespace Karry.Spikes.RlsPooling.Tests;

/// <summary>
/// One throwaway database per test run. The superuser connection string comes from
/// KARRY_SPIKE_PG (local: portable PostgreSQL; CI: postgres service container).
/// A missing variable fails the run loudly instead of silently skipping tests.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "KARRY_SPIKE_PG";

    public SpikeDatabase Database { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var admin = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(admin))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} is not set. Example: Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres");
        }

        Database = await SpikeDatabase.CreateAsync(admin, CancellationToken.None);
    }

    public async ValueTask DisposeAsync() => await Database.DropAsync();

    public NpgsqlDataSource AppDataSource(Action<NpgsqlConnectionStringBuilder>? configure = null) =>
        NpgsqlDataSource.Create(Database.ConnectionStringFor(SpikeDatabase.AppRole, configure));

    public NpgsqlDataSource OwnerDataSource() =>
        NpgsqlDataSource.Create(Database.ConnectionStringFor(SpikeDatabase.OwnerRole));

    /// <summary>Superuser connection: bypasses RLS. Used only to inspect ground truth.</summary>
    public NpgsqlDataSource AdminDataSource() => NpgsqlDataSource.Create(Database.AdminDatabaseConnectionString);
}
