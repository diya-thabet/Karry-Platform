using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Karry.BuildingBlocks.IntegrationTests;

/// <summary>
/// The tenant-isolation framework on real PostgreSQL with the real roles (US-P1-TEN-02, ADR-0004): RLS set per
/// command by the interceptor, the EF query filter, the save check, and the Phase 0 regressions (pooled connection
/// reuse, no session-level setting, WITH CHECK, FORCE, nullif). Each layer is also tested on its own, so removing
/// any one of them fails a test (mutation checks in the PR).
/// </summary>
[Collection(PostgresGroup.Name)]
[Trait("Story", "US-P1-TEN-02")]
public sealed class TenantIsolationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TenantId TenantA = new(Guid.Parse("01970000-0000-7000-8000-00000000000a"));
    private static readonly TenantId TenantB = new(Guid.Parse("01970000-0000-7000-8000-00000000000b"));

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 6, 0, 0, TimeSpan.Zero));
    private KarryDatabase database = null!;
    private ServiceProvider services = null!;

    public async ValueTask InitializeAsync()
    {
        database = await postgres.NewKarryDatabaseAsync();

        // Schema as the owner (as migrations would), then the RLS helper exactly as a migration calls it.
        var ownerOptions = new DbContextOptionsBuilder<TenantProbeDbContext>().UseNpgsql(database.Owner).UseSnakeCaseNamingConvention().Options;
        await using (var owner = new TenantProbeDbContext(ownerOptions))
        {
            await owner.Database.EnsureCreatedAsync();
            // Generated DDL with validated identifiers, no user input.
            string[] policies =
            [
                TenantRls.EnableSql(TenantProbeDbContext.SchemaName, "site_notes"),
                TenantRls.EnableSql(TenantProbeDbContext.SchemaName, "note_copies"),
            ];
            foreach (var ddl in policies)
            {
                await owner.Database.ExecuteSqlRawAsync(ddl);
            }
        }

        // One pooled connection, never reset on return: every unit of work reuses the previous one's session, as behind
        // PgBouncer in transaction mode. Npgsql's default reset (DISCARD ALL) would hide a session-level leak.
        services = BuildServices(new NpgsqlConnectionStringBuilder(database.App) { MaxPoolSize = 1, NoResetOnClose = true }.ConnectionString);
    }

    public async ValueTask DisposeAsync() => await services.DisposeAsync();

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-001")]
    public async Task A_tenant_sees_only_its_own_rows()
    {
        await SaveNotesAsync(TenantA, "A1", "A2");
        await SaveNotesAsync(TenantB, "B1");

        (await NotesAsync(TenantA)).Should().BeEquivalentTo("A1", "A2");
        (await NotesAsync(TenantB)).Should().BeEquivalentTo("B1");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-001")]
    public async Task Rls_alone_isolates_even_when_the_ef_filter_is_switched_off()
    {
        await SaveNotesAsync(TenantA, "A1");
        await SaveNotesAsync(TenantB, "B1");

        var seenByB = await InTenantAsync(TenantB, db => db.Notes.IgnoreQueryFilters().Select(n => n.Text).ToListAsync());

        seenByB.Should().Equal("B1");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-001")]
    public async Task The_ef_filter_alone_isolates_when_rls_does_not_apply()
    {
        await SaveNotesAsync(TenantA, "A1");
        await SaveNotesAsync(TenantB, "B1");

        // The administrator bypasses RLS: only the query filter stands between tenants here.
        await using var admin = BuildServices(database.Admin);
        var seenByB = await InTenantAsync(TenantB, db => db.Notes.Select(n => n.Text).ToListAsync(), admin);

        seenByB.Should().Equal("B1");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-TEN-001")]
    public async Task Without_a_tenant_nothing_is_visible()
    {
        await SaveNotesAsync(TenantA, "A1");

        (await InTenantAsync(null, db => db.Notes.IgnoreQueryFilters().CountAsync())).Should().Be(0, "RLS fails closed");
        (await RawCountAsync(database.App, "SELECT count(*) FROM tenantprobe.site_notes")).Should().Be(0);
    }

    [Fact]
    public async Task With_check_refuses_writing_a_row_for_another_tenant()
    {
        var act = () => InTenantAsync(TenantA, db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO tenantprobe.site_notes (id, tenant_id, text) VALUES ({Guid.CreateVersion7()}, {TenantB.Value}, 'smuggled')"));

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Saving_an_entity_of_another_tenant_is_refused_before_reaching_the_database()
    {
        var act = () => InTenantAsync(TenantA, async db =>
        {
            db.Notes.Add(SiteNote.Write(clock, TenantB, "smuggled"));
            return await db.SaveChangesAsync();
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another tenant*");
    }

    [Fact]
    public async Task A_reused_pooled_connection_never_carries_the_previous_tenant()
    {
        await SaveNotesAsync(TenantA, "A1");

        // Pool size 1, no reset: the next unit of work gets the same session that just ran as tenant A.
        (await NotesAsync(TenantA)).Should().Equal("A1");
        (await InTenantAsync(null, db => db.Notes.IgnoreQueryFilters().CountAsync())).Should().Be(0);
        (await InTenantAsync(null, db => db.Database.SqlQueryRaw<string>(
            "SELECT coalesce(nullif(current_setting('app.tenant_id', true), ''), '<none>') AS \"Value\"").SingleAsync()))
            .Should().Be("<none>", "the tenant is transaction-local, never a session setting (TC-P0-RLS-005)");
    }

    [Fact]
    public async Task Force_applies_rls_to_the_table_owner_too()
    {
        await SaveNotesAsync(TenantA, "A1");

        (await RawCountAsync(database.Owner, "SELECT count(*) FROM tenantprobe.site_notes")).Should().Be(0, "FORCE ROW LEVEL SECURITY (TC-P0-RLS-008)");
    }

    [Fact]
    public async Task Every_tenant_table_has_rls_enabled_forced_and_the_nullif_policy()
    {
        await using var connection = new NpgsqlConnection(database.Admin);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, p.qual, p.with_check
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_policies p ON p.schemaname = n.nspname AND p.tablename = c.relname
            WHERE n.nspname = 'tenantprobe' AND c.relname IN ('site_notes', 'note_copies')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = 0;
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows++;
            reader.GetBoolean(1).Should().BeTrue("RLS enabled");
            reader.GetBoolean(2).Should().BeTrue("RLS forced");
            reader.GetString(3).Should().Contain("NULLIF").And.Contain("app.tenant_id");
            reader.GetString(4).Should().Be(reader.GetString(3), "WITH CHECK equals USING");
        }

        rows.Should().Be(2);
    }

    [Fact]
    public async Task Subscribers_run_in_the_tenant_of_the_event()
    {
        await SaveNotesAsync(TenantA, "from A");
        await SaveNotesAsync(TenantB, "from B");

        var dispatched = await services.GetRequiredService<OutboxDispatcher<TenantProbeDbContext>>().DispatchPendingAsync(10, TestContext.Current.CancellationToken);

        dispatched.Should().Be(2);
        (await InTenantAsync(TenantA, db => db.Copies.Select(c => c.Text).ToListAsync())).Should().Equal("from A");
        (await InTenantAsync(TenantB, db => db.Copies.Select(c => c.Text).ToListAsync())).Should().Equal("from B");
        await using var admin = BuildServices(database.Admin);
        (await InTenantAsync(null, db => db.OutboxMessages.Select(m => m.TenantId).ToListAsync(), admin))
            .Should().BeEquivalentTo(new Guid?[] { TenantA.Value, TenantB.Value });
    }

    [Fact]
    public async Task The_tenant_of_a_unit_of_work_cannot_change()
    {
        var context = new TenantContext();
        context.Set(TenantA);

        context.Invoking(c => c.Set(TenantB)).Should().Throw<InvalidOperationException>();
        context.Invoking(c => c.Set(new TenantId(Guid.Empty))).Should().Throw<ArgumentException>();
        await Task.CompletedTask;
    }

    private ServiceProvider BuildServices(string connectionString)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddModuleDbContext<TenantProbeDbContext>(connectionString);
        collection.AddOutboxDispatcher<TenantProbeDbContext>();
        collection.AddScoped<IDomainEventSubscriber<NoteWritten>, NoteCopier>();
        return collection.BuildServiceProvider(validateScopes: true);
    }

    private async Task SaveNotesAsync(TenantId tenant, params string[] texts) =>
        await InTenantAsync(tenant, async db =>
        {
            foreach (var text in texts)
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                db.Notes.Add(SiteNote.Write(clock, tenant, text));
            }

            return await db.SaveChangesAsync();
        });

    private Task<List<string>> NotesAsync(TenantId tenant) =>
        InTenantAsync(tenant, db => db.Notes.OrderBy(n => n.Text).Select(n => n.Text).ToListAsync());

    private async Task<T> InTenantAsync<T>(TenantId? tenant, Func<TenantProbeDbContext, Task<T>> work, ServiceProvider? provider = null)
    {
        await using var scope = (provider ?? services).CreateAsyncScope();
        if (tenant is { } current)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(current);
        }

        return await work(scope.ServiceProvider.GetRequiredService<TenantProbeDbContext>());
    }

    private static async Task<long> RawCountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
