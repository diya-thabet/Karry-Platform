using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Karry.Host.Health;

/// <summary>Readiness: the database answers a trivial query. Failure details stay in logs, never in the response.</summary>
internal sealed partial class PostgresHealthCheck(IServiceProvider services, ILogger<PostgresHealthCheck> logger) : IHealthCheck
{
    /// <summary>
    /// A database that stops answering (frozen, or cut off by the network) must fail the probe within the load balancer's
    /// probe timeout, not after the 30 s command timeout: until then requests keep queueing behind a dead instance
    /// (P1.18a finding R-1, readiness took 60 s).
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return HealthCheckResult.Unhealthy("Database connection string 'Karry' is not configured.");
        }

        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            command.CommandTimeout = (int)Budget.TotalSeconds;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Budget);
            // WaitAsync: the answer comes on time even when the driver's own cancellation waits on the dead server.
            await command.ExecuteScalarAsync(budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            LogUnreachable(logger, ex);
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Readiness check could not reach PostgreSQL")]
    private static partial void LogUnreachable(ILogger logger, Exception exception);
}
