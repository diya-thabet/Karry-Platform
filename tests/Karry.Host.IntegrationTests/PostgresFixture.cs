using Testcontainers.PostgreSql;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// Real PostgreSQL for integration tests: the server in <c>KARRY_TEST_PG</c> when set (developer machines without
/// Docker), otherwise a Testcontainers instance of the same image as the local stack.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "timescale/timescaledb-ha:pg17.11-ts2.31.0";

    private PostgreSqlContainer? container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("KARRY_TEST_PG");
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
            return;
        }

        container = new PostgreSqlBuilder(Image).Build();
        await container.StartAsync().ConfigureAwait(false);
        ConnectionString = container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
