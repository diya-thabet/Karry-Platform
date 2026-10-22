using Karry.BuildingBlocks.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Karry.BuildingBlocks.Outbox;

/// <summary>Runs the dispatcher of one module in the Worker host: dispatch until empty, then wait one poll interval.</summary>
public sealed partial class OutboxDispatcherService<TContext>(
    OutboxDispatcher<TContext> dispatcher,
    OutboxOptions options,
    TimeProvider clock,
    ILogger<OutboxDispatcherService<TContext>> logger) : BackgroundService
    where TContext : ModuleDbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var attempted = await dispatcher.DispatchPendingAsync(500, stoppingToken).ConfigureAwait(false);
                if (attempted > 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Database unavailable or similar: keep the worker alive and try again after the poll interval.
                LogLoopFailure(logger, ex, typeof(TContext).Name);
            }

            await Task.Delay(options.PollInterval, clock, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error, Message = "Outbox dispatch loop for {Context} failed; retrying")]
    private static partial void LogLoopFailure(ILogger logger, Exception exception, string context);
}
