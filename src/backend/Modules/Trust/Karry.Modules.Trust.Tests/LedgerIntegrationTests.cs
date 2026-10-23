using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Domain;
using Karry.Modules.Trust.Infrastructure;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Karry.Modules.Trust.Tests;

// A tenant-owned test aggregate whose unit of work writes the ledger through the real PostgresLedgerWriter.

public readonly record struct BlastId(Guid Value) : IEntityId<BlastId>
{
    public static BlastId From(Guid value) => new(value);
}

[EventType("ledgerprobe.blast_recorded.v1")]
public sealed record BlastRecorded(Guid BlastId, decimal Tonnes) : DomainEvent;

public sealed class Blast : AggregateRoot<BlastId>, ITenantOwned
{
    private Blast(BlastId id, TenantId tenantId)
        : base(id) => TenantId = tenantId;

    public TenantId TenantId { get; private set; }

    public decimal Tonnes { get; private set; }

    public static Blast Record(TimeProvider clock, TenantId tenant, decimal tonnes)
    {
        var blast = new Blast(EntityId.New<BlastId>(clock), tenant) { Tonnes = tonnes };
        blast.Raise(new BlastRecorded(blast.Id.Value, tonnes) { OccurredAt = clock.GetUtcNow() });
        return blast;
    }
}

public sealed class LedgerProbeDbContext(DbContextOptions<LedgerProbeDbContext> options) : ModuleDbContext(options)
{
    public override string Schema => "ledgerprobe";

    public DbSet<Blast> Blasts => Set<Blast>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Blast>(blast =>
        {
            blast.ToTable("blasts");
            blast.HasKey(b => b.Id);
            blast.Property(b => b.Id).HasConversion(id => id.Value, value => BlastId.From(value));
            blast.Property(b => b.Tonnes).HasPrecision(19, 4);
            blast.Ignore(b => b.DomainEvents);
            blast.Ignore(b => b.AggregateKey);
        });
}

public sealed class LedgerDatabase : IAsyncLifetime
{
    public PostgresServer Server { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Server = await PostgresServer.StartAsync();

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>The ledger on real PostgreSQL with the real roles and the real migration (US-P1-AUD-01).</summary>
[Trait("Story", "US-P1-AUD-01")]
public sealed class LedgerIntegrationTests(LedgerDatabase postgres) : IClassFixture<LedgerDatabase>, IAsyncLifetime
{
    private static readonly TenantId TenantA = new(Guid.Parse("01970000-0000-7000-8000-00000000000a"));
    private static readonly TenantId TenantB = new(Guid.Parse("01970000-0000-7000-8000-00000000000b"));

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero).AddTicks(1234567));
    private KarryDatabase database = null!;
    private ServiceProvider services = null!;

    public async ValueTask InitializeAsync()
    {
        database = await postgres.Server.NewKarryDatabaseAsync();
        services = Build(database.App);

        // Trust schema from its real migration (as owner); the probe module's tables from its model + the RLS helper.
        await using (var migrations = new ServiceCollection().AddLogging().AddTrustInfrastructure(database.App).BuildServiceProvider())
        {
            await MigrationRunner.MigrateAsync(migrations, database.Owner, CancellationToken.None);
        }

        await using var owner = new LedgerProbeDbContext(new DbContextOptionsBuilder<LedgerProbeDbContext>()
            .UseNpgsql(database.Owner).UseSnakeCaseNamingConvention().Options);
        var ddl = owner.Database.GenerateCreateScript();
        await owner.Database.ExecuteSqlRawAsync(ddl);
        var rls = TenantRls.EnableSql("ledgerprobe", "blasts");
        await owner.Database.ExecuteSqlRawAsync(rls);
    }

    public async ValueTask DisposeAsync() => await services.DisposeAsync();

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-001")]
    public async Task Fifty_parallel_writers_produce_a_gap_free_valid_chain()
    {
        await Task.WhenAll(Enumerable.Range(1, 50).Select(i => SaveBlastAsync(TenantA, i)));

        var entries = await EntriesAsync(TenantA);
        entries.Select(e => e.Sequence).Should().Equal(Enumerable.Range(1, 50).Select(i => (long)i));
        LedgerVerifier.Verify(entries).IsValid.Should().BeTrue();
        (await ScalarAsync<long>(database.Admin, $"SELECT last_seq FROM audit.ledger_heads WHERE tenant_id = '{TenantA}'")).Should().Be(50);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-001")]
    public async Task A_writer_queued_behind_a_concurrent_append_continues_the_chain_instead_of_failing()
    {
        await SaveBlastAsync(TenantA, 1);
        var head = (await EntriesAsync(TenantA)).Single();

        // A concurrent writer, frozen mid-append: it locked the head and inserted entry 2, but has not moved the head yet
        // (the window where a writer reading the head without FOR UPDATE would pick sequence 2 again).
        await using var concurrent = new NpgsqlConnection(database.Admin);
        await concurrent.OpenAsync(TestContext.Current.CancellationToken);
        await using var open = await concurrent.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var payload = LedgerHash.CanonicalPayload("""{"concurrent":true}""");
        var payloadHash = LedgerHash.OfPayload(payload);
        var at = LedgerHash.ToStoragePrecision(clock.GetUtcNow());
        var hash = LedgerHash.OfEntry(head.EntryHash, TenantA, 2, "ledgerprobe.blast_recorded.v1", payloadHash, at);
        await using (var append = new NpgsqlCommand(
            $"""
            SELECT 1 FROM audit.ledger_heads WHERE tenant_id = '{TenantA}' FOR UPDATE;
            INSERT INTO audit.ledger_entries (tenant_id, seq, event_id, event_type, occurred_at, payload, payload_hash, prev_hash, entry_hash)
            VALUES ('{TenantA}', 2, '{Guid.CreateVersion7()}', 'ledgerprobe.blast_recorded.v1', @at, @payload, '{payloadHash}', '{head.EntryHash}', '{hash}');
            """, concurrent, open))
        {
            append.Parameters.AddWithValue("at", at);
            append.Parameters.AddWithValue("payload", payload);
            await append.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var writer = SaveBlastAsync(TenantA, 3); // must wait for the head lock, then read the moved head
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await using (var move = new NpgsqlCommand($"UPDATE audit.ledger_heads SET last_seq = 2, last_hash = '{hash}' WHERE tenant_id = '{TenantA}'", concurrent, open))
        {
            await move.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await open.CommitAsync(TestContext.Current.CancellationToken);
        await writer; // without FOR UPDATE it read the stale head (seq 1), chose seq 2 again and failed on the key

        var entries = await EntriesAsync(TenantA);
        entries.Select(e => e.Sequence).Should().Equal(1L, 2L, 3L);
        LedgerVerifier.Verify(entries).IsValid.Should().BeTrue();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-001")]
    public async Task Each_tenant_has_its_own_chain()
    {
        await SaveBlastAsync(TenantA, 1);
        await SaveBlastAsync(TenantB, 2);
        await SaveBlastAsync(TenantA, 3);

        (await EntriesAsync(TenantA)).Select(e => e.Sequence).Should().Equal(1L, 2L);
        (await EntriesAsync(TenantB)).Select(e => e.Sequence).Should().Equal(1L);
        LedgerVerifier.Verify(await EntriesAsync(TenantA)).IsValid.Should().BeTrue();
    }

    [Theory]
    [Trait("TestCase", "TC-P1-AUD-002")]
    [InlineData("UPDATE audit.ledger_entries SET payload = '{}'")]
    [InlineData("DELETE FROM audit.ledger_entries")]
    [InlineData("TRUNCATE audit.ledger_entries")]
    public async Task Nobody_can_change_or_delete_ledger_entries(string sql)
    {
        await SaveBlastAsync(TenantA, 1);

        foreach (var role in new[] { database.App, database.Owner })
        {
            var withTenant = $"SET app.tenant_id = '{TenantA}'; {sql}"; // past RLS: the refusal must come from privileges or triggers
            var act = () => ExecuteAsync(role, withTenant);
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.ledger_entries")).Should().Be(1);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-002")]
    public async Task The_app_role_holds_only_insert_and_select_on_the_ledger()
    {
        // Two independent layers: the missing privilege (this test) and the trigger (which also stops the owner).
        foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" })
        {
            (await ScalarAsync<bool>(database.Admin, $"SELECT has_table_privilege('karry_app', 'audit.ledger_entries', '{privilege}')"))
                .Should().BeFalse(privilege);
        }

        (await ScalarAsync<bool>(database.Admin, "SELECT has_table_privilege('karry_app', 'audit.ledger_entries', 'INSERT')")).Should().BeTrue();
        (await ScalarAsync<bool>(database.Admin, "SELECT has_table_privilege('karry_app', 'audit.ledger_entries', 'SELECT')")).Should().BeTrue();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-006")]
    public async Task A_rolled_back_business_change_leaves_no_ledger_entry_and_no_outbox_row()
    {
        await using (var scope = Scope(TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<LedgerProbeDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            db.Blasts.Add(Blast.Record(clock, TenantA, 120.5m));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM ledgerprobe.blasts")).Should().Be(0);
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM ledgerprobe.outbox_messages")).Should().Be(0);
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.ledger_entries")).Should().Be(0);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-006")]
    public async Task A_failing_ledger_write_rolls_back_the_business_change_and_its_outbox_row()
    {
        // 20 significant digits: not exactly representable as a double, so the strict canonicalizer refuses the payload.
        var act = () => SaveBlastAsync(TenantA, 0.12345678901234567890m);

        await act.Should().ThrowAsync<FormatException>();
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM ledgerprobe.blasts")).Should().Be(0);
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM ledgerprobe.outbox_messages")).Should().Be(0);
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.ledger_entries")).Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_cannot_read_another_tenants_ledger()
    {
        await SaveBlastAsync(TenantA, 1);

        (await EntriesAsync(TenantB)).Should().BeEmpty();
        await using var scope = Scope(TenantB);
        var trust = scope.ServiceProvider.GetRequiredService<TrustDbContext>();
        (await trust.LedgerEntries.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0, "RLS alone hides it");
    }

    [Fact]
    public async Task The_stored_row_recomputes_to_its_hash_despite_sub_microsecond_event_times()
    {
        await SaveBlastAsync(TenantA, 42);

        var entry = (await EntriesAsync(TenantA)).Single();
        entry.OccurredAt.Should().Be(LedgerHash.ToStoragePrecision(clock.GetUtcNow()));
        LedgerVerifier.Verify([entry]).IsValid.Should().BeTrue();
    }

    private ServiceProvider Build(string connectionString)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddTrustInfrastructure(connectionString);
        collection.AddModuleDbContext<LedgerProbeDbContext>(connectionString);
        return collection.BuildServiceProvider(validateScopes: true);
    }

    private AsyncServiceScope Scope(TenantId? tenant)
    {
        var scope = services.CreateAsyncScope();
        if (tenant is { } current)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(current);
        }

        return scope;
    }

    private async Task SaveBlastAsync(TenantId tenant, decimal tonnes)
    {
        await using var scope = Scope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<LedgerProbeDbContext>();
        db.Blasts.Add(Blast.Record(clock, tenant, tonnes));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<LedgerEntry>> EntriesAsync(TenantId tenant)
    {
        await using var scope = Scope(tenant);
        var trust = scope.ServiceProvider.GetRequiredService<TrustDbContext>();
        var rows = await trust.LedgerEntries.OrderBy(e => e.Seq).ToListAsync(TestContext.Current.CancellationToken);
        return [.. rows.Select(r => new LedgerEntry(r.TenantId, r.Seq, r.EventId, r.EventType, r.OccurredAt, r.Payload, r.PayloadHash, r.PrevHash, r.EntryHash))];
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
