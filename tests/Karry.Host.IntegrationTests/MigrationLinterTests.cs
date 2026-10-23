using Karry.BuildingBlocks.Persistence;
using Karry.Testing;
using Npgsql;

namespace Karry.Host.IntegrationTests;

/// <summary>Shares one PostgreSQL server across the linter tests.</summary>
public sealed class LinterDatabase : IAsyncLifetime
{
    public PostgresServer Server { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Server = await PostgresServer.StartAsync();

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>
/// Migration linter (US-P1-TEN-02 AC5, TC-P1-TEN-004): every table created by the real host's migrations follows the
/// tenant rules, and the linter catches each way a migration can break them.
/// </summary>
[Trait("Story", "US-P1-TEN-02")]
[Trait("TestCase", "TC-P1-TEN-004")]
public sealed class MigrationLinterTests(LinterDatabase postgres) : IClassFixture<LinterDatabase>
{
    private static readonly IReadOnlyList<MigrationLinter.Declaration> Declarations =
        MigrationLinter.ReadDeclarations(Path.Combine(PostgresServer.RepositoryRoot(), MigrationLinter.DeclarationsFile));

    [Fact]
    public async Task Every_table_of_the_hosts_migrations_has_tenant_rls_or_a_reasoned_declaration()
    {
        var database = await postgres.Server.NewKarryDatabaseAsync();
        await using (var factory = new HostFactory(database.App))
        {
            await MigrationRunner.MigrateAsync(factory.Services, database.Owner, TestContext.Current.CancellationToken);
        }

        var violations = await MigrationLinter.LintAsync(database.Admin, Declarations);

        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CREATE TABLE m.t (id uuid PRIMARY KEY)", "needs `tenant_id uuid NOT NULL`")]
    [InlineData("CREATE TABLE m.t (id uuid PRIMARY KEY, tenant_id uuid)", "needs `tenant_id uuid NOT NULL`")]
    [InlineData("CREATE TABLE m.t (id uuid PRIMARY KEY, tenant_id uuid NOT NULL)", "not enabled")]
    [InlineData("CREATE TABLE m.t (id uuid PRIMARY KEY, tenant_id uuid NOT NULL); ALTER TABLE m.t ENABLE ROW LEVEL SECURITY; " +
        "CREATE POLICY tenant_isolation ON m.t USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid) " +
        "WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)", "not FORCEd")]
    [InlineData("CREATE TABLE m.t (id uuid PRIMARY KEY, tenant_id uuid NOT NULL); ALTER TABLE m.t ENABLE ROW LEVEL SECURITY; " +
        "ALTER TABLE m.t FORCE ROW LEVEL SECURITY; CREATE POLICY tenant_isolation ON m.t USING (tenant_id = current_setting('app.tenant_id', true)::uuid)",
        "tenant_isolation policy")]
    [InlineData("CREATE TABLE m.t (id uuid PRIMARY KEY, tenant_id uuid NOT NULL); ALTER TABLE m.t ENABLE ROW LEVEL SECURITY; " +
        "ALTER TABLE m.t FORCE ROW LEVEL SECURITY; CREATE POLICY tenant_isolation ON m.t USING (true) WITH CHECK (true)", "tenant_isolation policy")]
    public async Task A_migration_that_breaks_the_tenant_rules_is_reported(string ddl, string expected)
    {
        var database = await postgres.Server.NewKarryDatabaseAsync();
        await ExecuteAsync(database.Owner, "CREATE SCHEMA m; " + ddl);

        var violations = await MigrationLinter.LintAsync(database.Admin, []);

        violations.Should().ContainSingle(v => v.StartsWith("m.t:", StringComparison.Ordinal) && v.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_table_built_with_the_rls_helper_passes()
    {
        var database = await postgres.Server.NewKarryDatabaseAsync();
        await ExecuteAsync(database.Owner, "CREATE SCHEMA m; CREATE TABLE m.t (id uuid PRIMARY KEY, tenant_id uuid NOT NULL);");
        await ExecuteAsync(database.Owner, BuildingBlocks.Tenancy.TenantRls.EnableSql("m", "t"));

        (await MigrationLinter.LintAsync(database.Admin, [])).Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_or_unexplained_declaration_is_reported()
    {
        var database = await postgres.Server.NewKarryDatabaseAsync();

        var violations = await MigrationLinter.LintAsync(database.Admin,
        [
            new("m", "gone", "global", "A table that was dropped long ago"),
            new("m", "t", "global", ""),
        ]);

        violations.Should().Contain(v => v.Contains("m.gone", StringComparison.Ordinal) && v.Contains("stale", StringComparison.Ordinal));
        violations.Should().Contain(v => v.StartsWith("m.t:", StringComparison.Ordinal) && v.Contains("declaration needs", StringComparison.Ordinal));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
