using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Karry.BuildingBlocks.IntegrationTests;

[Collection(PostgresGroup.Name)]
[Trait("Story", "US-P1-PLT-01")]
[Trait("TestCase", "TC-P1-PLT-007")]
public sealed class OutboxTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero));
    private readonly FailureSwitch failures = new();
    private ServiceProvider services = null!;

    private OutboxDispatcher<ProbeDbContext> Dispatcher => services.GetRequiredService<OutboxDispatcher<ProbeDbContext>>();

    public async ValueTask InitializeAsync() => await BuildAsync(withFlaky: true, maxAttempts: 10);

    public async ValueTask DisposeAsync() => await services.DisposeAsync();

    [Fact]
    public async Task The_change_and_its_outbox_rows_are_saved_together()
    {
        var probe = Probe.Create(clock, "excavator");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        probe.Rename(clock, "excavator EX-01");

        await using (var db = NewContext())
        {
            db.Probes.Add(probe);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        probe.DomainEvents.Should().BeEmpty("events are cleared once they are in the outbox");
        await using var check = NewContext();
        var messages = await check.OutboxMessages.OrderBy(m => m.OccurredAt).ToListAsync(TestContext.Current.CancellationToken);
        messages.Should().HaveCount(2);
        messages.Should().AllSatisfy(m =>
        {
            m.Type.Should().Be("probe.renamed.v1");
            m.Source.Should().Be("probe");
            m.AggregateKey.Should().Be($"Probe:{probe.Id.Value}");
            m.ProcessedAt.Should().BeNull();
        });
        var payload = System.Text.Json.JsonDocument.Parse(messages[1].Payload).RootElement;
        payload.GetProperty("name").GetString().Should().Be("excavator EX-01");
        payload.GetProperty("sequence").GetInt32().Should().Be(2);
        payload.GetProperty("eventId").GetGuid().Should().Be(messages[1].Id, "the outbox message ID is the event ID");
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_neither_the_change_nor_the_event()
    {
        await using (var db = NewContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            db.Probes.Add(Probe.Create(clock, "loader"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using var check = NewContext();
        (await check.Probes.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await check.OutboxMessages.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task A_failed_save_writes_no_event()
    {
        var probe = Probe.Create(clock, "truck");
        await SaveAsync(probe);

        await using (var db = NewContext())
        {
            var duplicate = await db.Probes.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            var clash = Probe.Create(clock, "clash");
            db.Entry(clash).Property(p => p.Id).CurrentValue = duplicate.Id; // primary key violation
            db.Probes.Add(clash);
            var save = () => db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await save.Should().ThrowAsync<DbUpdateException>();
        }

        await using var check = NewContext();
        (await check.OutboxMessages.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "only the first probe's event exists");
    }

    [Fact]
    public async Task Every_message_is_delivered_exactly_once()
    {
        await BuildAsync(withFlaky: false, maxAttempts: 10);
        await SaveAsync(Probe.Create(clock, "a"), Probe.Create(clock, "b"), Probe.Create(clock, "c"));

        (await Dispatcher.DispatchPendingAsync(100, TestContext.Current.CancellationToken)).Should().Be(3);
        (await Dispatcher.DispatchPendingAsync(100, TestContext.Current.CancellationToken)).Should().Be(0, "nothing is left");

        await using var check = NewContext();
        var logs = await check.Logs.ToListAsync(TestContext.Current.CancellationToken);
        logs.Should().HaveCount(3);
        logs.Select(l => l.MessageId).Should().OnlyHaveUniqueItems();
        (await check.OutboxMessages.AllAsync(m => m.ProcessedAt != null && m.Attempts == 1, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_rolls_back_every_subscriber_effect_and_retries_after_the_back_off()
    {
        failures.RemainingFailures = 1;
        await SaveAsync(Probe.Create(clock, "drill"));

        // Attempt 1: LogSubscriber writes its row, FlakySubscriber throws → the whole transaction rolls back.
        (await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken)).Should().Be(1);
        await using (var check = NewContext())
        {
            (await check.Logs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0, "the effect of the successful subscriber was rolled back");
            var message = await check.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
            message.Attempts.Should().Be(1);
            message.LastError.Should().Be("InvalidOperationException: Simulated downstream outage");
            message.NextAttemptAt.Should().Be(clock.GetUtcNow().AddSeconds(2));
        }

        (await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken)).Should().Be(0, "the retry waits for the back-off");
        clock.Advance(TimeSpan.FromSeconds(2));
        (await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken)).Should().Be(1);

        await using var final = NewContext();
        (await final.Logs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "applied exactly once despite the retry");
        (await final.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken)).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task After_the_last_attempt_a_message_is_dead_lettered_and_stops_blocking_its_aggregate()
    {
        await BuildAsync(withFlaky: true, maxAttempts: 3);
        failures.RemainingFailures = 3; // exactly the first message's three attempts fail
        var probe = Probe.Create(clock, "screen");
        await SaveAsync(probe);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await using (var db = NewContext())
        {
            db.Attach(probe);
            probe.Rename(clock, "screen 2");
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Dispatcher.DispatchPendingAsync(1, TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMinutes(10));
        }

        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken);
        await using var check = NewContext();
        var messages = await check.OutboxMessages.OrderBy(m => m.OccurredAt).ToListAsync(TestContext.Current.CancellationToken);
        messages[0].DeadLetteredAt.Should().NotBeNull();
        messages[0].Attempts.Should().Be(3);
        messages[0].ProcessedAt.Should().BeNull();
        messages[1].ProcessedAt.Should().NotBeNull("the next event of the aggregate is no longer blocked");
        (await check.Logs.Select(l => l.Sequence).ToListAsync(TestContext.Current.CancellationToken)).Should().Equal(2);
    }

    [Fact]
    public async Task An_unknown_event_type_is_dead_lettered_instead_of_blocking_the_worker()
    {
        await using (var db = NewContext())
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.CreateVersion7(),
                Type = "future.unknown_event.v9",
                Source = "probe",
                AggregateKey = "Other:1",
                OccurredAt = clock.GetUtcNow(),
                NextAttemptAt = clock.GetUtcNow(),
                Payload = "{}",
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken)).Should().Be(1);

        await using var check = NewContext();
        var message = await check.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        message.DeadLetteredAt.Should().NotBeNull();
        message.LastError.Should().Contain("future.unknown_event.v9");
    }

    [Fact]
    public async Task A_subscriber_already_recorded_for_a_message_is_not_run_again()
    {
        await BuildAsync(withFlaky: false, maxAttempts: 10);
        await SaveAsync(Probe.Create(clock, "pump"));
        await using (var db = NewContext())
        {
            var message = await db.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
            db.ProcessedMessages.Add(new ProcessedMessage { MessageId = message.Id, Handler = typeof(LogSubscriber).FullName!, ProcessedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken);

        await using var check = NewContext();
        (await check.Logs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await check.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken)).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Concurrent_dispatchers_deliver_each_message_once_and_keep_each_aggregates_order()
    {
        await BuildAsync(withFlaky: false, maxAttempts: 10);
        const int aggregates = 20, eventsPerAggregate = 10;
        var probes = Enumerable.Range(0, aggregates).Select(i => Probe.Create(clock, $"p{i}")).ToList();
        for (var round = 1; round < eventsPerAggregate; round++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            probes.ForEach(p => p.Rename(clock, $"{p.Name}+"));
        }

        await SaveAsync([.. probes]);

        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            // Keep polling until the outbox is empty: a worker may find nothing eligible while others hold locks.
            for (var idle = 0; idle < 20;)
            {
                idle = await Dispatcher.DispatchNextAsync(TestContext.Current.CancellationToken) ? 0 : idle + 1;
            }
        }));
        await Task.WhenAll(workers);

        await using var check = NewContext();
        var logs = await check.Logs.OrderBy(l => l.Id).ToListAsync(TestContext.Current.CancellationToken);
        logs.Should().HaveCount(aggregates * eventsPerAggregate);
        logs.Select(l => l.MessageId).Should().OnlyHaveUniqueItems("each message is handled exactly once");
        foreach (var group in logs.GroupBy(l => l.ProbeId))
        {
            group.Select(l => l.Sequence).Should().Equal(Enumerable.Range(1, eventsPerAggregate), "events of one aggregate are applied in order");
        }
    }

    [Fact]
    public async Task A_message_held_by_another_worker_is_skipped_not_waited_for_or_taken()
    {
        await BuildAsync(withFlaky: false, maxAttempts: 10);
        await SaveAsync(Probe.Create(clock, "held"));
        await using var otherWorker = await HoldMessageAsync(sequenceOfFirstProbe: 1);

        // Another worker holds the row lock: this dispatcher must neither take it nor block behind it.
        var dispatch = Dispatcher.DispatchNextAsync(TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(dispatch, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == dispatch;

        finished.Should().BeTrue("a locked message must be skipped (SKIP LOCKED), not waited for");
        (await dispatch).Should().BeFalse("the only message is held by another worker");
        await otherWorker.RollbackAsync(TestContext.Current.CancellationToken);
        await using var check = NewContext();
        (await check.Logs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task While_an_aggregates_earlier_event_is_in_flight_its_later_events_wait_but_other_aggregates_proceed()
    {
        await BuildAsync(withFlaky: false, maxAttempts: 10);
        var x = Probe.Create(clock, "x");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        x.Rename(clock, "x2");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var y = Probe.Create(clock, "y");
        await SaveAsync(x, y);

        await using (var otherWorker = await HoldMessageAsync(sequenceOfFirstProbe: 1, probeId: x.Id.Value))
        {
            await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken);
            await using var during = NewContext();
            (await during.Logs.Select(l => l.ProbeId).ToListAsync(TestContext.Current.CancellationToken))
                .Should().Equal([y.Id.Value], "x's second event must wait for its first; y is independent");
            await otherWorker.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken);
        await using var after = NewContext();
        var xSequences = await after.Logs.Where(l => l.ProbeId == x.Id.Value).OrderBy(l => l.Id).Select(l => l.Sequence)
            .ToListAsync(TestContext.Current.CancellationToken);
        xSequences.Should().Equal(1, 2);
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task A_connection_lost_in_the_middle_of_a_delivery_rolls_it_back_and_the_message_is_delivered_once_later()
    {
        // Hardening plan H-R5: the database session dies after one subscriber wrote its effect, before the commit.
        await BuildAsync(withFlaky: false, maxAttempts: 10, withConnectionKiller: true);
        failures.RemainingFailures = 1;
        var probe = Probe.Create(clock, "cut");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        probe.Rename(clock, "cut+");
        await SaveAsync(probe);

        // Whether the dispatcher records the failure or the loop catches it (OutboxDispatcherService), nothing is lost.
        try
        {
            await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or DbUpdateException)
        {
        }

        await using (var during = NewContext())
        {
            (await during.Logs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0, "the effect written before the cut was never committed");
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        (await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken)).Should().Be(2);

        await using var after = NewContext();
        var logs = await after.Logs.OrderBy(l => l.Id).ToListAsync(TestContext.Current.CancellationToken);
        logs.Select(l => l.Sequence).Should().Equal([1, 2], "both events delivered once, in order, after the cut");
        (await after.OutboxMessages.AllAsync(m => m.ProcessedAt != null, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public void Back_off_doubles_and_is_capped()
    {
        var options = new OutboxOptions();

        options.DelayAfter(1).Should().Be(TimeSpan.FromSeconds(2));
        options.DelayAfter(2).Should().Be(TimeSpan.FromSeconds(4));
        options.DelayAfter(5).Should().Be(TimeSpan.FromSeconds(32));
        options.DelayAfter(30).Should().Be(TimeSpan.FromMinutes(10));
    }

    private string connectionString = string.Empty;

    private async Task BuildAsync(bool withFlaky, int maxAttempts, bool withConnectionKiller = false)
    {
        if (services is not null)
        {
            await services.DisposeAsync();
        }

        connectionString = postgres.NewDatabase();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddSingleton(failures);
        collection.AddModuleDbContext<ProbeDbContext>(connectionString);
        collection.AddOutboxDispatcher<ProbeDbContext>(new OutboxOptions { MaxAttempts = maxAttempts });
        collection.AddScoped<IDomainEventSubscriber<ProbeRenamed>, LogSubscriber>();
        if (withFlaky)
        {
            collection.AddScoped<IDomainEventSubscriber<ProbeRenamed>, FlakySubscriber>();
        }

        if (withConnectionKiller)
        {
            collection.AddScoped<IDomainEventSubscriber<ProbeRenamed>, ConnectionKillingSubscriber>();
        }

        services = collection.BuildServiceProvider(validateScopes: true);
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    /// <summary>Simulates another worker mid-dispatch: an open transaction holding the row lock of one message.</summary>
    private async Task<HeldRowLock> HoldMessageAsync(int sequenceOfFirstProbe, Guid? probeId = null)
    {
        await using var db = NewContext();
        var messages = await db.OutboxMessages.AsNoTracking().OrderBy(m => m.OccurredAt).ToListAsync(TestContext.Current.CancellationToken);
        var target = messages.First(m =>
            System.Text.Json.JsonDocument.Parse(m.Payload).RootElement.GetProperty("sequence").GetInt32() == sequenceOfFirstProbe
            && (probeId is null || m.AggregateKey == $"Probe:{probeId}"));

        // Another worker's own connection (the context's data source does not reveal its password, by design).
        var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("SELECT id FROM probe.outbox_messages WHERE id = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", target.Id);
        await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return new HeldRowLock(connection, transaction);
    }

    /// <summary>An open transaction holding a row lock; disposing releases the lock and closes the connection.</summary>
    private sealed class HeldRowLock(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public Task RollbackAsync(CancellationToken cancellationToken) => transaction.RollbackAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private ProbeDbContext NewContext() => services.CreateScope().ServiceProvider.GetRequiredService<ProbeDbContext>();

    private async Task SaveAsync(params Probe[] probes)
    {
        await using var db = NewContext();
        db.Probes.AddRange(probes);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
