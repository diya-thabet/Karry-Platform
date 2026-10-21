using Npgsql;

namespace Karry.Spikes.RlsPooling.Tests;

/// <summary>
/// WP-P0-12: Row-Level Security + Npgsql connection pooling.
/// Expected results are stated from the requirement (tenant isolation, fail closed),
/// never derived from the implementation.
/// </summary>
public sealed class TenantIsolationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact, Trait("TestCase", "TC-P0-RLS-001"), Trait("WorkPackage", "WP-P0-12")]
    public async Task Tenant_sees_only_its_own_rows()
    {
        await using var app = fixture.AppDataSource();
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        await SeedAsync(app, tenantA, rows: 3);
        await SeedAsync(app, tenantB, rows: 2);

        await using var scope = await TenantScope.BeginAsync(app, tenantA, Ct);
        var visible = await Db.VisibleTenantIdsAsync(scope, Ct);

        Assert.Equal(3, visible.Count);
        Assert.All(visible, t => Assert.Equal(tenantA, t));
    }

    [Fact, Trait("TestCase", "TC-P0-RLS-002"), Trait("WorkPackage", "WP-P0-12")]
    public async Task Insert_for_another_tenant_is_rejected_by_with_check()
    {
        await using var app = fixture.AppDataSource();
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(app, tenantA, Ct);
        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.InsertEntryAsync(scope, tenantB, 10m, Ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState); // 42501: violates row-level security policy
    }

    [Fact, Trait("TestCase", "TC-P0-RLS-003"), Trait("WorkPackage", "WP-P0-12")]
    public async Task Missing_tenant_context_fails_closed_on_fresh_connection()
    {
        await using var app = fixture.AppDataSource();
        await SeedAsync(app, Guid.CreateVersion7(), rows: 2);

        await using var connection = await app.OpenConnectionAsync(Ct);
        var visible = await Db.UnscopedVisibleTenantIdsAsync(connection, Ct);
        Assert.Empty(visible);

        await using var insert = new NpgsqlCommand(
            "INSERT INTO spike.shift_entry (id, tenant_id, meter_value) VALUES (gen_random_uuid(), gen_random_uuid(), 1)", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Theory, Trait("TestCase", "TC-P0-RLS-004"), Trait("WorkPackage", "WP-P0-12")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_tenant_context_on_a_previously_scoped_session_returns_no_rows_without_error(bool noResetOnClose)
    {
        // Same physical session reused: after SET LOCAL ends, current_setting(...,true) returns ''
        // rather than NULL. The policy must still fail closed (zero rows) instead of throwing on ''::uuid.
        await using var app = fixture.AppDataSource(b => { b.MaxPoolSize = 1; b.NoResetOnClose = noResetOnClose; });
        var tenant = Guid.CreateVersion7();
        await SeedAsync(app, tenant, rows: 2);

        await using var connection = await app.OpenConnectionAsync(Ct);
        var visible = await Db.UnscopedVisibleTenantIdsAsync(connection, Ct);

        Assert.Empty(visible);
    }

    [Fact, Trait("TestCase", "TC-P0-RLS-005"), Trait("WorkPackage", "WP-P0-12")]
    public async Task Hazard_session_level_setting_leaks_to_next_pool_user_when_reset_is_disabled()
    {
        // Negative control documenting WHY session-level SET is forbidden: with connection reset
        // disabled (a common performance tweak, and the normal behaviour behind transaction-mode
        // PgBouncer), a session-level tenant setting survives into the next request.
        await using var app = fixture.AppDataSource(b => { b.MaxPoolSize = 1; b.NoResetOnClose = true; });
        var tenantA = Guid.CreateVersion7();
        await SeedAsync(app, tenantA, rows: 2);

        await using (var first = await app.OpenConnectionAsync(Ct))
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, false)", first);
            set.Parameters.AddWithValue(tenantA.ToString());
            await set.ExecuteScalarAsync(Ct);
        }

        await using var second = await app.OpenConnectionAsync(Ct);
        var leaked = await Db.UnscopedVisibleTenantIdsAsync(second, Ct);

        Assert.Equal(2, leaked.Count); // the next "request" sees tenant A without having set any tenant
    }

    [Fact, Trait("TestCase", "TC-P0-RLS-006"), Trait("WorkPackage", "WP-P0-12")]
    public async Task Transaction_local_setting_does_not_leak_even_when_reset_is_disabled()
    {
        await using var app = fixture.AppDataSource(b => { b.MaxPoolSize = 1; b.NoResetOnClose = true; });
        var tenantA = Guid.CreateVersion7();
        await SeedAsync(app, tenantA, rows: 2);

        await using (var scope = await TenantScope.BeginAsync(app, tenantA, Ct))
        {
            Assert.Equal(2, (await Db.VisibleTenantIdsAsync(scope, Ct)).Count);
        }

        await using var next = await app.OpenConnectionAsync(Ct);
        Assert.Empty(await Db.UnscopedVisibleTenantIdsAsync(next, Ct));
    }

    [Fact, Trait("TestCase", "TC-P0-RLS-007"), Trait("WorkPackage", "WP-P0-12")]
    public async Task App_role_is_not_owner_and_cannot_bypass_rls()
    {
        await using var admin = fixture.AdminDataSource();

        var bypass = await Db.CountAsync(admin,
            "SELECT count(*) FROM pg_roles WHERE rolname = $1 AND (rolbypassrls OR rolsuper)", Ct, SpikeDatabase.AppRole);
        var owned = await Db.CountAsync(admin,
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'spike' AND tableowner = $1", Ct, SpikeDatabase.AppRole);

        Assert.Equal(0, bypass);
        Assert.Equal(0, owned);
    }

    [Fact, Trait("TestCase", "TC-P0-RLS-008"), Trait("WorkPackage", "WP-P0-12")]
    public async Task Table_owner_is_filtered_only_when_rls_is_forced()
    {
        await using var owner = fixture.OwnerDataSource();
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        await using var admin = fixture.AdminDataSource();
        foreach (var (table, tenant) in new[] { ("shift_entry", tenantA), ("shift_entry", tenantB), ("unforced_entry", tenantA), ("unforced_entry", tenantB) })
        {
            var columns = table == "shift_entry" ? "(id, tenant_id, meter_value) VALUES (gen_random_uuid(), $1, 1)" : "(id, tenant_id) VALUES (gen_random_uuid(), $1)";
            await using var seed = admin.CreateCommand($"INSERT INTO spike.{table} {columns}");
            seed.Parameters.AddWithValue(tenant);
            await seed.ExecuteNonQueryAsync(Ct);
        }

        await using var scope = await TenantScope.BeginAsync(owner, tenantA, Ct);
        var forced = await Db.VisibleTenantIdsAsync(scope, Ct);
        var unforced = await Db.VisibleTenantIdsAsync(scope, Ct, "spike.unforced_entry");

        Assert.All(forced, t => Assert.Equal(tenantA, t));        // FORCE: owner is filtered too
        Assert.Contains(tenantB, unforced);                       // no FORCE: owner sees every tenant
    }

    [Fact, Trait("TestCase", "TC-P0-RLS-009"), Trait("WorkPackage", "WP-P0-12")]
    public async Task Concurrent_mixed_tenant_load_never_returns_another_tenants_rows()
    {
        // WP-P0-12 acceptance: 10,000 concurrent mixed-tenant requests with zero cross-tenant leakage.
        const int operations = 10_000;
        const int tenantsCount = 50;
        await using var app = fixture.AppDataSource(b => { b.MaxPoolSize = 20; b.NoResetOnClose = true; });
        var tenants = Enumerable.Range(0, tenantsCount).Select(_ => Guid.CreateVersion7()).ToArray();
        var inserted = new int[tenantsCount];
        var leaks = 0;

        await Parallel.ForAsync(0, operations, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = Ct }, async (i, ct) =>
        {
            var index = Random.Shared.Next(tenantsCount);
            await using var scope = await TenantScope.BeginAsync(app, tenants[index], ct);
            if (i % 2 == 0)
            {
                await Db.InsertEntryAsync(scope, tenants[index], i, ct);
                Interlocked.Increment(ref inserted[index]);
            }

            await using var command = scope.CreateCommand("SELECT tenant_id FROM spike.shift_entry WHERE tenant_id <> $1");
            command.Parameters.AddWithValue(tenants[index]);
            var foreign = await Db.ReadTenantIdsAsync(command, ct);
            Interlocked.Add(ref leaks, foreign.Count);
            await scope.CommitAsync(ct);
        });

        Assert.Equal(0, leaks);
        await using var admin = fixture.AdminDataSource();
        for (var t = 0; t < tenantsCount; t++)
        {
            Assert.Equal(inserted[t], await Db.CountAsync(admin, "SELECT count(*) FROM spike.shift_entry WHERE tenant_id = $1", Ct, tenants[t]));
        }
    }

    [Fact, Trait("TestCase", "TC-P0-RLS-010"), Trait("WorkPackage", "WP-P0-12")]
    public async Task Empty_tenant_id_is_refused_before_touching_the_database()
    {
        await using var app = fixture.AppDataSource();
        await Assert.ThrowsAsync<ArgumentException>(() => TenantScope.BeginAsync(app, Guid.Empty, Ct));
    }

    private static async Task SeedAsync(NpgsqlDataSource app, Guid tenant, int rows)
    {
        await using var scope = await TenantScope.BeginAsync(app, tenant, Ct);
        for (var i = 0; i < rows; i++)
        {
            await Db.InsertEntryAsync(scope, tenant, i, Ct);
        }

        await scope.CommitAsync(Ct);
    }
}
