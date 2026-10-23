using Npgsql;
using Testcontainers.PostgreSql;

namespace Karry.BuildingBlocks.IntegrationTests;

/// <summary>One PostgreSQL server per test run (Testcontainers, or <c>KARRY_TEST_PG</c>); each test gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "timescale/timescaledb-ha:pg17.11-ts2.31.0";

    private PostgreSqlContainer? container;
    private string serverConnectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("KARRY_TEST_PG");
        if (!string.IsNullOrWhiteSpace(external))
        {
            serverConnectionString = external;
            return;
        }

        container = new PostgreSqlBuilder(Image).Build();
        await container.StartAsync();
        serverConnectionString = container.GetConnectionString();
    }

    /// <summary>Connection string to a new, empty database (created by EnsureCreated).</summary>
    public string NewDatabase() =>
        new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = $"t_{Guid.NewGuid():N}", Pooling = true }.ConnectionString;

    /// <summary>
    /// A new database prepared exactly like an environment: <c>deploy/database/roles.sql</c> run by an administrator,
    /// so tests use the real <c>karry_owner</c> (migrations) and <c>karry_app</c> (runtime) roles.
    /// </summary>
    public async Task<KarryDatabase> NewKarryDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        await using (var server = new NpgsqlConnection(serverConnectionString))
        {
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", server);
            await create.ExecuteNonQueryAsync();
        }

        var admin = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = name }.ConnectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var roles = new NpgsqlCommand(
                $"SET karry.owner_password = '{OwnerPassword}'; SET karry.app_password = '{AppPassword}';\n" +
                await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "deploy", "database", "roles.sql")),
                connection);
            await roles.ExecuteNonQueryAsync();
        }

        return new KarryDatabase(
            admin,
            new NpgsqlConnectionStringBuilder(admin) { Username = "karry_owner", Password = OwnerPassword }.ConnectionString,
            new NpgsqlConnectionStringBuilder(admin) { Username = "karry_app", Password = AppPassword }.ConnectionString);
    }

    // Test-only passwords; roles are created once per server (they are cluster-wide) and re-granted per database.
    private const string OwnerPassword = "owner-test-only";
    private const string AppPassword = "app-test-only";

    private static string RepositoryRoot()
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
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}

/// <summary>Connection strings of one test database: administrator, schema owner, runtime application role.</summary>
public sealed record KarryDatabase(string Admin, string Owner, string App);

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
