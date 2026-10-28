using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.BuildingBlocks.Persistence;

/// <summary>
/// Applies the EF Core migrations of every registered module database as the schema owner (<c>karry_owner</c>).
/// Runs as its own step (<c>Karry.Host migrate</c>), never at application start-up: the runtime role cannot change
/// the schema, and a deployment runs migrations once before rolling out (P1.17). Idempotent.
/// </summary>
public static partial class MigrationRunner
{
    /// <summary>
    /// EF Core builds a context's design-time model lazily, once per process, and reading it while another thread builds
    /// it fails ("The model must be finalized…"). Hosts in one process (test classes run in parallel) therefore migrate
    /// one context type at a time; different modules still migrate side by side.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, SemaphoreSlim> Gates = new();

    public static async Task<int> MigrateAsync(IServiceProvider services, string ownerConnectionString, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Karry.Migrations");

        var databases = services.GetServices<ModuleDatabase>().DistinctBy(d => d.ContextType).ToList();
        foreach (var database in databases)
        {
            var gate = Gates.GetOrAdd(database.ContextType, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var context = database.CreateForMigration(ownerConnectionString);
                var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                LogMigrated(logger, context.Schema, pending.Count);
            }
            finally
            {
                gate.Release();
            }
        }

        return databases.Count;
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Schema {Schema}: {Count} migration(s) applied")]
    private static partial void LogMigrated(ILogger logger, string schema, int count);
}
