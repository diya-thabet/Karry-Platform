using System.Collections.Concurrent;
using Npgsql;

namespace Karry.BuildingBlocks.Persistence;

/// <summary>
/// One <see cref="NpgsqlDataSource"/>, so one connection pool, per connection string in the process: every module
/// context, idempotency and the health check share it. Before (P1.18a load test H-L1), Npgsql kept one pool per
/// configuration (plain contexts, the spatial Organization context, the host's own data source), each allowed up to
/// <c>Maximum Pool Size</c> (100) connections, so 500 tablets at once took more connections than PostgreSQL has and got
/// <c>53300 too many clients</c>. With one pool, <c>Maximum Pool Size</c> in the connection string is the process's real
/// budget, and a burst waits for a free connection (up to <c>Timeout</c>) instead of being refused by the server.
/// </summary>
public sealed class KarryDataSources(IEnumerable<DataSourcePlugin> plugins) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> sources = new(StringComparer.Ordinal);

    public NpgsqlDataSource For(string connectionString) =>
        sources.GetOrAdd(connectionString, key => new Lazy<NpgsqlDataSource>(() => Build(key))).Value;

    public async ValueTask DisposeAsync()
    {
        foreach (var source in sources.Values.Where(s => s.IsValueCreated))
        {
            await source.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    private NpgsqlDataSource Build(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        foreach (var plugin in plugins)
        {
            plugin.Configure(builder);
        }

        return builder.Build();
    }
}

/// <summary>A type mapping a module needs on the shared data source (Organization: PostGIS geometries).</summary>
public sealed record DataSourcePlugin(Action<NpgsqlDataSourceBuilder> Configure);
