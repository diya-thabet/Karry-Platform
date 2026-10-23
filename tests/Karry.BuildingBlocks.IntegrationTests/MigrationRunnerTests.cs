using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Persistence;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.BuildingBlocks.IntegrationTests;

/// <summary>Database roles and the migration runner (US-P1-TEN-02 AC3, ADR-0004): owner migrates, app only reads and writes data.</summary>
[Collection(PostgresGroup.Name)]
[Trait("Story", "US-P1-TEN-02")]
public sealed class MigrationRunnerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_create_the_schema_owned_by_karry_owner_with_history_in_the_module_schema()
    {
        var database = await postgres.NewKarryDatabaseAsync();

        var migrated = await MigrateAsync(database);

        migrated.Should().Be(1, "the platform database is registered");
        (await ScalarAsync<string>(database.Admin, "SELECT tableowner FROM pg_tables WHERE schemaname = 'platform' AND tablename = 'idempotency_records'"))
            .Should().Be("karry_owner");
        (await ScalarAsync<string>(database.Admin, "SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = 'platform'"))
            .Should().Be("karry_owner");
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM platform.__ef_migrations_history")).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Running_the_migrations_again_changes_nothing()
    {
        var database = await postgres.NewKarryDatabaseAsync();
        await MigrateAsync(database);
        var before = await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM platform.__ef_migrations_history");

        var act = () => MigrateAsync(database);

        await act.Should().NotThrowAsync();
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM platform.__ef_migrations_history")).Should().Be(before);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-008")]
    public async Task The_app_role_reads_and_writes_data_but_cannot_change_the_schema()
    {
        var database = await postgres.NewKarryDatabaseAsync();
        await MigrateAsync(database);

        await ExecuteAsync(database.App,
            "INSERT INTO platform.idempotency_records (scope, key, request_fingerprint, created_at, expires_at) VALUES ('s', 'k', 'f', now(), now())");
        (await ScalarAsync<long>(database.App, "SELECT count(*) FROM platform.idempotency_records")).Should().Be(1);

        foreach (var ddl in new[]
        {
            "CREATE TABLE platform.sneaky (id int)",
            "CREATE TABLE public.sneaky (id int)",
            "CREATE SCHEMA sneaky",
            "ALTER TABLE platform.idempotency_records ADD COLUMN x int",
            "DROP TABLE platform.idempotency_records",
            "TRUNCATE platform.idempotency_records",
        })
        {
            var act = () => ExecuteAsync(database.App, ddl);
            (await act.Should().ThrowAsync<PostgresException>($"karry_app must not run: {ddl}"))
                .Which.SqlState.Should().BeOneOf(PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.InvalidSchemaName);
        }
    }

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-008")]
    public async Task The_app_role_is_no_superuser_cannot_bypass_rls_and_owns_nothing()
    {
        var database = await postgres.NewKarryDatabaseAsync();
        await MigrateAsync(database);

        (await ScalarAsync<string>(database.Admin, "SELECT rolsuper::text || ',' || rolbypassrls::text FROM pg_roles WHERE rolname = 'karry_app'"))
            .Should().Be("false,false");
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM pg_class WHERE relowner = 'karry_app'::regrole"))
            .Should().Be(0, "a table owner bypasses non-forced RLS");
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM pg_namespace WHERE nspowner = 'karry_app'::regrole"))
            .Should().Be(0);
    }

    private static async Task<int> MigrateAsync(KarryDatabase database)
    {
        var services = new ServiceCollection().AddLogging().AddIdempotency(database.App).BuildServiceProvider();
        await using (services)
        {
            return await MigrationRunner.MigrateAsync(services, database.Owner, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
