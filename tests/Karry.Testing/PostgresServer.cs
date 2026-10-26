using System.Security.Cryptography;
using System.Text;
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
    private readonly Lazy<Task<string>> extensionsTemplate;

    private PostgresServer(string connectionString, PostgreSqlContainer? container)
    {
        ServerConnectionString = connectionString;
        this.container = container;
        extensionsTemplate = new(() => CreateExtensionsTemplateAsync(connectionString, Path.Combine(RepositoryRoot(), "deploy", "database")));
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
    /// A new database prepared exactly like an environment: <c>deploy/database/extensions.sql</c> and <c>roles.sql</c> run by an administrator,
    /// so tests use the real <c>karry_owner</c> (migrations) and <c>karry_app</c> (runtime) roles.
    /// </summary>
    public async Task<KarryDatabase> NewKarryDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        var database = Path.Combine(RepositoryRoot(), "deploy", "database");
        var template = await extensionsTemplate.Value.ConfigureAwait(false);
        await using (var server = new NpgsqlConnection(ServerConnectionString))
        {
            await server.OpenAsync().ConfigureAwait(false);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE {template}", server);
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Each test gets its own database and pools; a small pool per database keeps the server (100 slots) from running
        // out when many test databases are alive, while concurrency tests still run in parallel (they queue fairly).
        var admin = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = name, MaxPoolSize = 20 }.ConnectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var roles = await File.ReadAllTextAsync(Path.Combine(database, "roles.sql")).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"SET karry.owner_password = '{OwnerPassword}'; SET karry.app_password = '{AppPassword}';\n{roles}", connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return new KarryDatabase(
            admin,
            new NpgsqlConnectionStringBuilder(admin) { Username = "karry_owner", Password = OwnerPassword }.ConnectionString,
            new NpgsqlConnectionStringBuilder(admin) { Username = "karry_app", Password = AppPassword }.ConnectionString);
    }

    /// <summary>
    /// A template database with <c>extensions.sql</c> applied, made once per server: creating PostGIS and TimescaleDB in
    /// every test database is slow enough to time fixtures out under parallel load, while copying a template is fast.
    /// The name carries a hash of the file, so a shared server (<c>KARRY_TEST_PG</c>) never reuses a stale template.
    /// </summary>
    private static async Task<string> CreateExtensionsTemplateAsync(string serverConnectionString, string database)
    {
        var extensions = await File.ReadAllTextAsync(Path.Combine(database, "extensions.sql")).ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(extensions)))[..12];
        var template = $"karry_extensions_{hash}";

        await using var server = new NpgsqlConnection(serverConnectionString);
        await server.OpenAsync().ConfigureAwait(false);

        // Test runs of several projects may share one server: one of them creates the template, the others wait.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(727274)", server) { CommandTimeout = 0 })
        {
            await lockCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        try
        {
            await using var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", server);
            exists.Parameters.AddWithValue("name", template);
            if ((long)(await exists.ExecuteScalarAsync().ConfigureAwait(false))! == 0)
            {
                // Built under a temporary name and renamed, so a run that stops half way never leaves a broken template.
                var building = $"{template}_building";
                await using (var leftover = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @building", server))
                {
                    leftover.Parameters.AddWithValue("building", building);
                    if ((long)(await leftover.ExecuteScalarAsync().ConfigureAwait(false))! > 0)
                    {
                        await using var drop = new NpgsqlCommand(
                            $"ALTER DATABASE {building} WITH IS_TEMPLATE false; DROP DATABASE {building} WITH (FORCE);", server);
                        await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }

                await using (var create = new NpgsqlCommand($"CREATE DATABASE {building}", server))
                {
                    await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                var builder = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = building, Pooling = false };
                await using (var connection = new NpgsqlConnection(builder.ConnectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    // Creating PostGIS and TimescaleDB takes long when many test projects start their servers at once.
                    await using var command = new NpgsqlCommand(extensions, connection) { CommandTimeout = 600 };
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // TimescaleDB starts a background scheduler in each database that has the extension; a template must have
                // no sessions, or copying it fails ("source database is being accessed by other users").
                await using (var close = new NpgsqlCommand(
                    $"ALTER DATABASE {building} WITH ALLOW_CONNECTIONS false IS_TEMPLATE true; " +
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @building AND pid <> pg_backend_pid();", server))
                {
                    close.Parameters.AddWithValue("building", building);
                    await close.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await WaitUntilNoSessionsAsync(server, building).ConfigureAwait(false);
                await using var rename = new NpgsqlCommand($"ALTER DATABASE {building} RENAME TO {template}", server);
                await rename.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(727274)", server);
            await unlock.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return template;
    }

    private static async Task WaitUntilNoSessionsAsync(NpgsqlConnection server, string name)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", server);
            sessions.Parameters.AddWithValue("name", name);
            if ((long)(await sessions.ExecuteScalarAsync().ConfigureAwait(false))! == 0)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Database {name} still has sessions after 10 seconds.");
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
