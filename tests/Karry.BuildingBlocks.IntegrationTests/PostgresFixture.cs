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

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
