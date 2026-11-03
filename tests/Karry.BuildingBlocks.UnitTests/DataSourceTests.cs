using Karry.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// One connection pool per process (load finding L-1, docs/05-quality/load-testing.md): every module context on the
/// same connection string uses the same <see cref="NpgsqlDataSource"/>, so <c>Maximum Pool Size</c> is the process's
/// real budget, and the plugins modules need (PostGIS) are on it.
/// </summary>
[Trait("Story", "P1.18a")]
public sealed class DataSourceTests
{
    private const string Database = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Maximum Pool Size=7";

    [Fact]
    public async Task Every_module_context_shares_one_data_source()
    {
        var services = new ServiceCollection();
        services.AddModuleDbContext<FirstContext>(Database);
        services.AddModuleDbContext<SecondContext>(Database);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var first = DataSourceOf(scope.ServiceProvider.GetRequiredService<FirstContext>());
        var second = DataSourceOf(scope.ServiceProvider.GetRequiredService<SecondContext>());

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Same(first, provider.GetRequiredService<KarryDataSources>().For(Database));
        Assert.Equal(7, new NpgsqlConnectionStringBuilder(first.ConnectionString).MaxPoolSize);
    }

    [Fact]
    public async Task Another_connection_string_gets_its_own_pool_and_plugins_run_once_per_pool()
    {
        var calls = 0;
        await using var sources = new KarryDataSources([new DataSourcePlugin(_ => Interlocked.Increment(ref calls))]);

        var runtime = sources.For(Database);
        Assert.Same(runtime, sources.For(Database));
        Assert.NotSame(runtime, sources.For(Database.Replace("Maximum Pool Size=7", "Maximum Pool Size=3", StringComparison.Ordinal)));
        Assert.Equal(2, calls);
    }

    // The only way to see which data source a context was given; acceptable in a test that pins this wiring.
#pragma warning disable EF1001 // internal EF Core API
    private static NpgsqlDataSource? DataSourceOf(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<NpgsqlOptionsExtension>()?.DataSource as NpgsqlDataSource;
#pragma warning restore EF1001

    public sealed class FirstContext(DbContextOptions<FirstContext> options) : ModuleDbContext(options)
    {
        public override string Schema => "first";

        protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
        {
        }
    }

    public sealed class SecondContext(DbContextOptions<SecondContext> options) : ModuleDbContext(options)
    {
        public override string Schema => "second";

        protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
        {
        }
    }
}
