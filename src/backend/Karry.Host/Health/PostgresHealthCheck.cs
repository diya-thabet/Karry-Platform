using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Karry.Host.Health;

/// <summary>Readiness: the database answers a trivial query. Failure details stay in logs, never in the response.</summary>
internal sealed partial class PostgresHealthCheck(IServiceProvider services, ILogger<PostgresHealthCheck> logger) : IHealthCheck
{
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
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
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
