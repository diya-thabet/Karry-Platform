using Karry.Testing;

namespace Karry.BuildingBlocks.IntegrationTests;

/// <summary>One PostgreSQL server per test run (<see cref="PostgresServer"/>); each test gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgresServer? server;

    public async ValueTask InitializeAsync() => server = await PostgresServer.StartAsync();

    /// <summary>Connection string to a new, empty database (created by EnsureCreated).</summary>
    public string NewDatabase() => server!.NewDatabase();

    /// <inheritdoc cref="PostgresServer.NewKarryDatabaseAsync"/>
    public Task<KarryDatabase> NewKarryDatabaseAsync() => server!.NewKarryDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
