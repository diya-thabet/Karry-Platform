using System.Diagnostics;
using System.Reflection;
using Karry.BuildingBlocks.Observability;
using Karry.BuildingBlocks.Persistence;
using Karry.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.BuildingBlocks.Outbox;

/// <summary>Retry policy of the dispatcher: exponential back-off, then dead-letter.</summary>
public sealed record OutboxOptions
{
    public int MaxAttempts { get; init; } = 10;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Delay before attempt <paramref name="attempt"/> + 1: 2 s, 4 s, 8 s … capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan DelayAfter(int attempt) =>
        TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * (1L << Math.Clamp(attempt - 1, 0, 30))));
}

/// <summary>
/// Dispatches the outbox of one module (ADR-0015, ADR-0018). Each message is handled in its own transaction:
/// claim it with <c>FOR UPDATE SKIP LOCKED</c> (several workers can run), run every handler not yet recorded in
/// <c>processed_messages</c>, mark it processed, commit. A failure rolls everything back and schedules a retry.
/// Events of one aggregate are dispatched strictly in order: a message is only eligible when no earlier message of
/// the same aggregate is still pending (including one being processed by another worker).
/// </summary>
public sealed partial class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopes,
    EventSerializer serializer,
    TimeProvider clock,
    OutboxOptions options,
    OutboxMetrics metrics,
    ILogger<OutboxDispatcher<TContext>> logger)
    where TContext : ModuleDbContext
{
    private const string Processed = "processed";
    private const string Retried = "retried";
    private const string DeadLettered = "dead_lettered";

    private static readonly MethodInfo HandleTypedMethod =
        typeof(OutboxDispatcher<TContext>).GetMethod(nameof(HandleTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Dispatches eligible messages until none is left or <paramref name="maxMessages"/> is reached; returns how many were attempted.</summary>
    public async Task<int> DispatchPendingAsync(int maxMessages, CancellationToken cancellationToken)
    {
        var attempted = 0;
        while (attempted < maxMessages && await DispatchNextAsync(cancellationToken).ConfigureAwait(false))
        {
            attempted++;
        }

        return attempted;
    }

    /// <summary>Claims and dispatches one eligible message; false when there is none.</summary>
    public async Task<bool> DispatchNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var message = await ClaimNextAsync(db, now, cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return false;
        }

        using var activity = OutboxMetrics.ActivitySource.StartActivity("outbox dispatch", ActivityKind.Consumer);
        activity?.SetTag(TelemetryAttributes.Module, db.Schema);
        activity?.SetTag(TelemetryAttributes.EventType, message.Type);

        var domainEvent = serializer.Deserialize(message.Type, message.Payload);
        if (domainEvent is null)
        {
            message.Attempts++;
            message.DeadLetteredAt = now;
            message.LastError = $"Unknown event type '{message.Type}'.";
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            LogDeadLettered(logger, message.Id, message.Type, message.LastError);
            metrics.Record(db.Schema, DeadLettered);
            activity?.SetStatus(ActivityStatusCode.Error, "unknown event type");
            return true;
        }

        try
        {
            var context = new DomainEventContext(message.Id, db);
            await ((Task)HandleTypedMethod.MakeGenericMethod(domainEvent.GetType())
                .Invoke(null, [scope.ServiceProvider, db, domainEvent, context, now, cancellationToken])!).ConfigureAwait(false);

            message.Attempts++;
            message.ProcessedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            metrics.Record(db.Schema, Processed);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("exception.type", ex.GetType().FullName);
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await RecordFailureAsync(db.Schema, message.Id, ex, now, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// Publishes the age of the oldest pending message (gauge <c>karry.outbox.oldest_pending_age</c>). Called by the
    /// dispatcher loop on every pass, so a stuck or failing subscriber shows up as growing lag.
    /// </summary>
    public async Task MeasureBacklogAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var oldest = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null)
            .MinAsync(m => (DateTimeOffset?)m.OccurredAt, cancellationToken)
            .ConfigureAwait(false);
        metrics.SetOldestPendingAge(db.Schema, oldest is null ? TimeSpan.Zero : clock.GetUtcNow() - oldest.Value);
    }

    private static async Task<OutboxMessage?> ClaimNextAsync(TContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The schema is validated by ModuleDbContext (lowercase letters and underscores) and quoted; values are parameters.
        var table = $"\"{db.Schema}\".outbox_messages";
        var sql =
            $"""
            SELECT o.* FROM {table} o
            WHERE o.processed_at IS NULL AND o.dead_lettered_at IS NULL AND o.next_attempt_at <= @now
              AND NOT EXISTS (
                SELECT 1 FROM {table} e
                WHERE e.aggregate_key = o.aggregate_key AND e.processed_at IS NULL AND e.dead_lettered_at IS NULL
                  AND (e.occurred_at, e.id) < (o.occurred_at, o.id))
            ORDER BY o.occurred_at, o.id
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """;
        return await db.OutboxMessages
            .FromSqlRaw(sql, new Npgsql.NpgsqlParameter("now", now))
            .AsTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task HandleTypedAsync<TEvent>(
        IServiceProvider services, TContext db, TEvent domainEvent, DomainEventContext context, DateTimeOffset now, CancellationToken cancellationToken)
        where TEvent : DomainEvent
    {
        foreach (var handler in services.GetServices<IDomainEventSubscriber<TEvent>>())
        {
            var handlerName = handler.GetType().FullName!;
            var alreadyDone = await db.ProcessedMessages
                .AnyAsync(p => p.MessageId == context.MessageId && p.Handler == handlerName, cancellationToken)
                .ConfigureAwait(false);
            if (alreadyDone)
            {
                continue;
            }

            await handler.HandleAsync(domainEvent, context, cancellationToken).ConfigureAwait(false);
            db.ProcessedMessages.Add(new ProcessedMessage { MessageId = context.MessageId, Handler = handlerName, ProcessedAt = now });
        }
    }

    private async Task RecordFailureAsync(string module, Guid messageId, Exception exception, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var message = await db.OutboxMessages.SingleAsync(m => m.Id == messageId, cancellationToken).ConfigureAwait(false);

        message.Attempts++;
        message.LastError = Truncate($"{exception.GetType().Name}: {exception.Message}", 2000);
        if (message.Attempts >= options.MaxAttempts)
        {
            message.DeadLetteredAt = now;
            LogDeadLettered(logger, message.Id, message.Type, message.LastError);
        }
        else
        {
            message.NextAttemptAt = now + options.DelayAfter(message.Attempts);
            LogRetry(logger, exception, message.Id, message.Type, message.Attempts, message.NextAttemptAt);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        metrics.Record(module, message.DeadLetteredAt is null ? Retried : DeadLettered);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) failed on attempt {Attempt}; retry at {NextAttemptAt}")]
    private static partial void LogRetry(ILogger logger, Exception exception, Guid messageId, string type, int attempt, DateTimeOffset nextAttemptAt);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Outbox message {MessageId} ({Type}) dead-lettered: {Reason}")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, string type, string reason);
}
