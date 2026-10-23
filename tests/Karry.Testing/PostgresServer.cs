using Npgsql;
using Testcontainers.PostgreSql;

namespace Karry.Testing;

/// <summary>
/// One PostgreSQL server per test run: the server in <c>KARRY_TEST_PG</c> when set (developer machines without
/// Docker), otherwise a Testcontainers instance of the local-stack image. Each test gets its own database.
/// </summary>
public sealed class PostgresServer : IAsyncDisposable
{
    public const string Image = "timescale/timescaledb-ha:pg17.11-ts2.31.0";

    // Test-only passwords; roles are cluster-wide (created once) and granted per database.
    private const string OwnerPassword = "owner-test-only";
    private const string AppPassword = "app-test-only";

    private readonly PostgreSqlContainer? container;

    private PostgresServer(string connectionString, PostgreSqlContainer? container)
    {
        ServerConnectionString = connectionString;
        this.container = container;
    }

    public string ServerConnectionString { get; }

    public static async Task<PostgresServer> StartAsync()
    {
        var external = Environment.GetEnvironmentVariable("KARRY_TEST_PG");
        if (!string.IsNullOrWhiteSpace(external))
        {
            return new PostgresServer(external, null);
        }

        var container = new PostgreSqlBuilder(Image).Build();
        await container.StartAsync().ConfigureAwait(false);
        return new PostgresServer(container.GetConnectionString(), container);
    }

    /// <summary>Connection string to a new, empty database (the caller creates the schema, e.g. EnsureCreated).</summary>
    public string NewDatabase() =>
        new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = $"t_{Guid.NewGuid():N}", Pooling = true }.ConnectionString;

    /// <summary>
    /// A new database prepared exactly like an environment: <c>deploy/database/roles.sql</c> run by an administrator,
    /// so tests use the real <c>karry_owner</c> (migrations) and <c>karry_app</c> (runtime) roles.
    /// </summary>
    public async Task<KarryDatabase> NewKarryDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        await using (var server = new NpgsqlConnection(ServerConnectionString))
        {
            await server.OpenAsync().ConfigureAwait(false);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", server);
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Each test gets its own database and pools; a small pool per database keeps the server (100 slots) from running
        // out when many test databases are alive, while concurrency tests still run in parallel (they queue fairly).
        var admin = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = name, MaxPoolSize = 20 }.ConnectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var roles = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "deploy", "database", "roles.sql")).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"SET karry.owner_password = '{OwnerPassword}'; SET karry.app_password = '{AppPassword}';\n{roles}", connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return new KarryDatabase(
            admin,
            new NpgsqlConnectionStringBuilder(admin) { Username = "karry_owner", Password = OwnerPassword }.ConnectionString,
            new NpgsqlConnectionStringBuilder(admin) { Username = "karry_app", Password = AppPassword }.ConnectionString);
    }

    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Karry.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Karry.slnx) not found.");
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (container is not null)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Connection strings of one test database: administrator, schema owner, runtime application role.</summary>
public sealed record KarryDatabase(string Admin, string Owner, string App);
