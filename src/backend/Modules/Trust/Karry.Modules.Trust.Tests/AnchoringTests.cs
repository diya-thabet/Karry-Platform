using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Domain;
using Karry.Modules.Trust.Infrastructure;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Karry.Modules.Trust.Tests;

/// <summary>
/// WORM anchoring (US-P1-AUD-02) and verification against anchors (US-P1-AUD-03 AC1) on the real database and the real
/// signer, with an in-memory object store that keeps every version (as a versioned Object Lock bucket does).
/// </summary>
[Trait("Story", "US-P1-AUD-02")]
public sealed class AnchoringTests(LedgerDatabase postgres) : IClassFixture<LedgerDatabase>, IAsyncLifetime
{
    private static readonly TenantId TenantA = new(Guid.Parse("01970000-0000-7000-8000-00000000000a"));
    private static readonly TenantId TenantB = new(Guid.Parse("01970000-0000-7000-8000-00000000000b"));

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero));
    private readonly VersionedStore store = new();
    private readonly SoftwareAnchorSigner signer = new(ECDsa.Create(ECCurve.NamedCurves.nistP256));
    private KarryDatabase database = null!;
    private ServiceProvider services = null!;

    public async ValueTask InitializeAsync()
    {
        database = await postgres.Server.NewKarryDatabaseAsync();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddTrustInfrastructure(database.App);
        collection.AddModuleDbContext<LedgerProbeDbContext>(database.App);
        collection.AddSingleton<ITenantDirectory>(new FixedDirectory(TenantA, TenantB));
        collection.AddSingleton<IAnchorStore>(store);
        collection.AddSingleton<IAnchorSigner>(signer);
        collection.AddSingleton<IAnchorVerifier>(signer);
        collection.AddMetrics();
        collection.AddSingleton<AnchoringMetrics>();
        collection.AddSingleton<AnchorLedgers>();
        services = collection.BuildServiceProvider(validateScopes: true);

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

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        signer.Dispose();
        NpgsqlConnection.ClearAllPools();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-004")]
    public async Task After_a_three_hour_outage_one_run_anchors_every_tenants_current_head()
    {
        await WriteAsync(TenantA, 3);
        clock.Advance(TimeSpan.FromHours(1));
        await WriteAsync(TenantB, 2);
        clock.Advance(TimeSpan.FromHours(2)); // no anchoring for three hours
        await WriteAsync(TenantA, 1);

        var run = await RunAsync();

        run.Should().Be(new AnchoringRun(Tenants: 2, Anchored: 2, Failed: 0));
        store.Keys.Should().BeEquivalentTo($"{TenantA}/{4L:D20}.json", $"{TenantB}/{2L:D20}.json");
        (await VerifyAsync(TenantA)).Should().Match<LedgerVerificationReport>(r => r.Valid && r.EntriesChecked == 4 && r.AnchorsChecked == 1);
        (await VerifyAsync(TenantB)).Should().Match<LedgerVerificationReport>(r => r.Valid && r.AnchorsChecked == 1);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-AUD-004")]
    public async Task A_run_anchors_only_heads_that_moved()
    {
        await WriteAsync(TenantA, 2);
        (await RunAsync()).Anchored.Should().Be(1);

        (await RunAsync()).Should().Be(new AnchoringRun(2, 0, 0), "nothing new: no second anchor and no failed attempt");
        await WriteAsync(TenantA, 1);
        (await RunAsync()).Should().Be(new AnchoringRun(2, 1, 0));

        store.Keys.Should().HaveCount(2);
        store.VersionCounts.Should().OnlyContain(count => count == 1, "an unchanged head is never written again");
    }

    [Fact]
    public async Task One_tenants_storage_failure_does_not_stop_the_others()
    {
        await WriteAsync(TenantA, 1);
        await WriteAsync(TenantB, 1);
        store.FailFor = TenantA;

        var run = await RunAsync();

        run.Should().Be(new AnchoringRun(2, 1, 1));
        store.FailFor = null;
        (await RunAsync()).Anchored.Should().Be(1, "the next run catches up the tenant that failed");
    }

    [Fact]
    [Trait("Story", "US-P1-AUD-03")]
    public async Task A_consistently_rewritten_chain_passes_the_hash_check_but_not_the_anchor()
    {
        await WriteAsync(TenantA, 3);
        await RunAsync();

        // Someone with full database access rewrites entry 2 and recomputes every hash after it: the chain is
        // internally consistent again, only the WORM anchor still holds the original head.
        await RewriteChainFromAsync(TenantA, 2);

        var report = await VerifyAsync(TenantA);

        report.Valid.Should().BeFalse();
        report.FirstBrokenSequence.Should().Be(3);
        report.Problem.Should().Contain("differs from its WORM anchor");
    }

    [Fact]
    [Trait("Story", "US-P1-AUD-03")]
    public async Task Entries_removed_after_their_anchor_are_reported()
    {
        await WriteAsync(TenantA, 3);
        await RunAsync();
        await AdminAsync($"SET session_replication_role = replica; DELETE FROM audit.ledger_entries WHERE tenant_id = '{TenantA}' AND seq = 3;");

        var report = await VerifyAsync(TenantA);

        report.Valid.Should().BeFalse();
        report.Problem.Should().Contain("sequence 3 was anchored (entries removed)");
    }

    [Fact]
    [Trait("Story", "US-P1-AUD-03")]
    public async Task A_newer_version_of_an_anchor_object_is_reported_as_overwritten()
    {
        await WriteAsync(TenantA, 2);
        await RunAsync();
        store.AddVersion(store.Keys.Single(), "{}");

        (await VerifyAsync(TenantA)).Problem.Should().Contain("was overwritten (2 versions)");
    }

    [Fact]
    [Trait("Story", "US-P1-AUD-03")]
    public async Task An_anchor_signed_by_an_unknown_key_is_reported()
    {
        await WriteAsync(TenantA, 2);
        using var foreign = new SoftwareAnchorSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var forged = new LedgerAnchor(TenantA, 2, new string('a', 64), clock.GetUtcNow(), foreign.KeyId);
        await store.PutAsync(forged, forged.ToDocument(foreign.Sign(forged.SignedBytes())), CancellationToken.None);

        (await VerifyAsync(TenantA)).Problem.Should().Contain("invalid signature");
    }

    [Fact]
    public void Anchor_documents_round_trip_and_the_signature_covers_every_field()
    {
        var anchor = new LedgerAnchor(TenantA, 42, new string('c', 64), clock.GetUtcNow(), signer.KeyId);
        var parsed = LedgerAnchor.FromDocument(anchor.ToDocument(signer.Sign(anchor.SignedBytes())))!.Value;

        parsed.Anchor.Should().Be(anchor);
        signer.Verify(anchor.KeyId, anchor.SignedBytes(), parsed.Signature).Should().BeTrue();
        signer.Verify(anchor.KeyId, (anchor with { Sequence = 43 }).SignedBytes(), parsed.Signature).Should().BeFalse();
        signer.Verify(anchor.KeyId, (anchor with { EntryHash = new string('d', 64) }).SignedBytes(), parsed.Signature).Should().BeFalse();
        anchor.ObjectKey.Should().Be($"{TenantA}/00000000000000000042.json");
    }

    private Task<AnchoringRun> RunAsync() => services.GetRequiredService<AnchorLedgers>().RunAsync(TestContext.Current.CancellationToken);

    private async Task<LedgerVerificationReport> VerifyAsync(TenantId tenant)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
        return await scope.ServiceProvider.GetRequiredService<VerifyLedgerHandler>().HandleAsync(TestContext.Current.CancellationToken);
    }

    private async Task WriteAsync(TenantId tenant, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
            var db = scope.ServiceProvider.GetRequiredService<LedgerProbeDbContext>();
            clock.Advance(TimeSpan.FromSeconds(1));
            db.Blasts.Add(Blast.Record(clock, tenant, 10 + i));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Changes the payload of <paramref name="from"/> and recomputes every later hash and the head, as an attacker would.</summary>
    private async Task RewriteChainFromAsync(TenantId tenant, long from)
    {
        await using var connection = new NpgsqlConnection(database.Admin);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var replica = new NpgsqlCommand("SET session_replication_role = replica", connection))
        {
            await replica.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var rows = new List<(long Seq, string Type, DateTimeOffset At, string Payload, string Prev)>();
        await using (var read = new NpgsqlCommand($"SELECT seq, event_type, occurred_at, payload, prev_hash FROM audit.ledger_entries WHERE tenant_id = '{tenant}' ORDER BY seq", connection))
        await using (var reader = await read.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3), reader.GetString(4)));
            }
        }

        var previous = rows.Single(r => r.Seq == from).Prev;
        foreach (var row in rows.Where(r => r.Seq >= from))
        {
            var payload = row.Seq == from ? LedgerHash.CanonicalPayload(row.Payload.Replace("\"tonnes\":", "\"tonnes\":9", StringComparison.Ordinal)) : row.Payload;
            var payloadHash = LedgerHash.OfPayload(payload);
            var hash = LedgerHash.OfEntry(previous, tenant, row.Seq, row.Type, payloadHash, row.At);
            await using var update = new NpgsqlCommand(
                "UPDATE audit.ledger_entries SET payload = @p, payload_hash = @ph, prev_hash = @prev, entry_hash = @h WHERE tenant_id = @t AND seq = @s", connection);
            update.Parameters.AddWithValue("p", payload);
            update.Parameters.AddWithValue("ph", payloadHash);
            update.Parameters.AddWithValue("prev", previous);
            update.Parameters.AddWithValue("h", hash);
            update.Parameters.AddWithValue("t", tenant.Value);
            update.Parameters.AddWithValue("s", row.Seq);
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            previous = hash;
        }

        await using var head = new NpgsqlCommand($"UPDATE audit.ledger_heads SET last_hash = @h WHERE tenant_id = '{tenant}'", connection);
        head.Parameters.AddWithValue("h", previous);
        await head.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task AdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.Admin);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FixedDirectory(params TenantId[] tenants) : ITenantDirectory
    {
        public Task<IReadOnlyList<TenantId>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TenantId>>(tenants);
    }

    /// <summary>Keeps every version per key, like a versioned Object Lock bucket.</summary>
    private sealed class VersionedStore : IAnchorStore
    {
        private readonly ConcurrentDictionary<string, List<string>> objects = new(StringComparer.Ordinal);

        public TenantId? FailFor { get; set; }

        public IEnumerable<string> Keys => objects.Keys;

        public IEnumerable<int> VersionCounts => objects.Values.Select(v => v.Count);

        public Task PutAsync(LedgerAnchor anchor, string document, CancellationToken cancellationToken)
        {
            if (anchor.Tenant == FailFor)
            {
                throw new IOException("storage unavailable");
            }

            AddVersion(anchor.ObjectKey, document);
            return Task.CompletedTask;
        }

        public void AddVersion(string key, string document) => objects.GetOrAdd(key, _ => []).Add(document);

        public async IAsyncEnumerable<StoredAnchor> ListAsync(TenantId tenant, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var (key, versions) in objects.Where(o => o.Key.StartsWith($"{tenant}/", StringComparison.Ordinal)).OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                yield return new StoredAnchor(versions[0], versions.Count);
            }
        }
    }
}
