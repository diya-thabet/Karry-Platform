// WP-P0-12 benchmark: latency overhead of RLS + per-transaction set_config versus an
// explicit WHERE tenant_id filter on an identical table without RLS.
// Usage: KARRY_SPIKE_PG="Host=...;Username=postgres;Password=..." dotnet run -c Release
using System.Diagnostics;
using Karry.Spikes.RlsPooling;
using Npgsql;

var admin = Environment.GetEnvironmentVariable("KARRY_SPIKE_PG")
    ?? throw new InvalidOperationException("Set KARRY_SPIKE_PG to a superuser connection string.");
const int tenants = 50, rowsPerTenant = 2_000, iterations = 20_000, parallelism = 32;

var db = await SpikeDatabase.CreateAsync(admin, CancellationToken.None);
try
{
    var tenantIds = Enumerable.Range(0, tenants).Select(_ => Guid.CreateVersion7()).ToArray();
    await using (var adminDs = NpgsqlDataSource.Create(db.AdminDatabaseConnectionString))
    {
        foreach (var table in new[] { "shift_entry", "shift_entry_no_rls" })
        {
            await using var seed = adminDs.CreateCommand(
                $"INSERT INTO spike.{table} (id, tenant_id, meter_value) SELECT gen_random_uuid(), t, g FROM unnest($1::uuid[]) t, generate_series(1, $2) g");
            seed.Parameters.AddWithValue(tenantIds);
            seed.Parameters.AddWithValue(rowsPerTenant);
            await seed.ExecuteNonQueryAsync();
            await using var analyze = adminDs.CreateCommand($"ANALYZE spike.{table}");
            await analyze.ExecuteNonQueryAsync();
        }
    }

    await using var app = NpgsqlDataSource.Create(db.ConnectionStringFor(SpikeDatabase.AppRole, b => b.MaxPoolSize = parallelism));

    async Task<double[]> Measure(Func<Guid, Task> op)
    {
        var samples = new double[iterations];
        await Parallel.ForAsync(0, iterations, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (i, _) =>
        {
            var sw = Stopwatch.StartNew();
            await op(tenantIds[i % tenants]);
            samples[i] = sw.Elapsed.TotalMilliseconds;
        });
        Array.Sort(samples);
        return samples;
    }

    async Task Rls(Guid tenant)
    {
        await using var scope = await TenantScope.BeginAsync(app, tenant, CancellationToken.None);
        await using var cmd = scope.CreateCommand("SELECT count(*), sum(meter_value) FROM spike.shift_entry");
        await cmd.ExecuteScalarAsync();
        await scope.CommitAsync(CancellationToken.None);
    }

    async Task NoRls(Guid tenant)
    {
        await using var conn = await app.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*), sum(meter_value) FROM spike.shift_entry_no_rls WHERE tenant_id = $1", conn, tx);
        cmd.Parameters.AddWithValue(tenant);
        await cmd.ExecuteScalarAsync();
        await tx.CommitAsync();
    }

    // Same extra set_config round trip as the RLS path, but on the table without RLS:
    // isolates the cost of the round trip from the cost of evaluating the policy.
    async Task NoRlsWithSetConfig(Guid tenant)
    {
        await using var conn = await app.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", conn, tx))
        {
            set.Parameters.AddWithValue(tenant.ToString());
            await set.ExecuteScalarAsync();
        }

        await using var cmd = new NpgsqlCommand("SELECT count(*), sum(meter_value) FROM spike.shift_entry_no_rls WHERE tenant_id = $1", conn, tx);
        cmd.Parameters.AddWithValue(tenant);
        await cmd.ExecuteScalarAsync();
        await tx.CommitAsync();
    }

    // Mitigation: send set_config and the first query in ONE round trip (NpgsqlBatch).
    async Task RlsBatched(Guid tenant)
    {
        await using var conn = await app.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var batch = new NpgsqlBatch(conn, tx);
        var set = new NpgsqlBatchCommand("SELECT set_config('app.tenant_id', $1, true)");
        set.Parameters.AddWithValue(tenant.ToString());
        batch.BatchCommands.Add(set);
        batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT count(*), sum(meter_value) FROM spike.shift_entry"));
        await using (var reader = await batch.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) { }
            await reader.NextResultAsync();
            while (await reader.ReadAsync()) { }
        }

        await tx.CommitAsync();
    }

    var variants = new (string Name, Func<Guid, Task> Op)[]
    {
        ("A baseline: explicit WHERE, no RLS", NoRls),
        ("B no RLS + set_config round trip", NoRlsWithSetConfig),
        ("C RLS + TenantScope (2 round trips)", Rls),
        ("D RLS + batched set_config (1 round trip)", RlsBatched),
    };
    foreach (var v in variants) { await Measure(v.Op); } // warm-up
    var results = new List<(string Name, double[] Samples)>();
    foreach (var v in variants) { results.Add((v.Name, await Measure(v.Op))); }

    static double P(double[] s, double q) => s[(int)(q * (s.Length - 1))];
    var baseline = results[0].Samples;
    Console.WriteLine($"iterations={iterations} parallelism={parallelism} rows={tenants * rowsPerTenant}");
    foreach (var (name, s) in results)
    {
        Console.WriteLine($"{name,-44} p50={P(s, .5):F3}ms p95={P(s, .95):F3}ms mean={s.Average():F3}ms  vs A: {(s.Average() / baseline.Average() - 1) * 100:+0.0;-0.0}%");
    }

    var policyCost = (results[2].Samples.Average() / results[1].Samples.Average() - 1) * 100;
    Console.WriteLine($"Policy evaluation cost alone (C vs B): {policyCost:+0.0;-0.0}%");
}
finally
{
    await db.DropAsync();
}
