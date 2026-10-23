using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Karry.BuildingBlocks.Observability;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Karry.BuildingBlocks.IntegrationTests;

/// <summary>Outbox metrics behind the job-lag dashboard and the <c>KarryOutboxLagHigh</c> alert (US-P1-OPS-01 AC2).</summary>
[Collection(PostgresGroup.Name)]
[Trait("Story", "US-P1-OPS-01")]
public sealed class OutboxMetricsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 6, 0, 0, TimeSpan.Zero));
    private readonly FailureSwitch failures = new();
    private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> measurements = new();
    private ServiceProvider services = null!;
    private MeterListener listener = null!;

    private OutboxDispatcher<ProbeDbContext> Dispatcher => services.GetRequiredService<OutboxDispatcher<ProbeDbContext>>();

    public async ValueTask InitializeAsync()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddSingleton(failures);
        collection.AddModuleDbContext<ProbeDbContext>(postgres.NewDatabase());
        collection.AddOutboxDispatcher<ProbeDbContext>(new OutboxOptions { MaxAttempts = 2 });
        collection.AddScoped<IDomainEventSubscriber<ProbeRenamed>, FlakySubscriber>();
        services = collection.BuildServiceProvider(validateScopes: true);

        // Only this provider's meters: other tests create their own "Karry.Outbox" meters.
        var factory = services.GetRequiredService<IMeterFactory>();
        listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OutboxMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags));
        listener.Start();

        _ = services.GetRequiredService<OutboxMetrics>(); // creates the instruments
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ProbeDbContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        listener.Dispose();
        await services.DisposeAsync();
    }

    [Fact]
    public async Task Backlog_age_is_the_age_of_the_oldest_pending_message_and_zero_once_dispatched()
    {
        await SaveAsync(Probe.Create(clock, "excavator"));
        clock.Advance(TimeSpan.FromMinutes(2));
        await SaveAsync(Probe.Create(clock, "loader"));
        clock.Advance(TimeSpan.FromMinutes(5));

        await Dispatcher.MeasureBacklogAsync(TestContext.Current.CancellationToken);
        OldestPendingAge().Should().Be(420, "the first message waits since 7 minutes (above the 5-minute alert threshold)");

        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken);
        await Dispatcher.MeasureBacklogAsync(TestContext.Current.CancellationToken);
        OldestPendingAge().Should().Be(0);
    }

    [Fact]
    public async Task Dead_lettered_messages_do_not_count_as_backlog()
    {
        failures.RemainingFailures = 2; // MaxAttempts = 2 → dead-lettered
        await SaveAsync(Probe.Create(clock, "crusher"));
        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(10));
        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken);

        await Dispatcher.MeasureBacklogAsync(TestContext.Current.CancellationToken);

        OldestPendingAge().Should().Be(0, "dead letters are alerted on separately; they would otherwise hold the lag alert forever");
    }

    [Fact]
    public async Task Every_dispatch_outcome_is_counted_per_module()
    {
        failures.RemainingFailures = 1;
        await SaveAsync(Probe.Create(clock, "drill"));
        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken); // fails → retried
        clock.Advance(TimeSpan.FromSeconds(2));
        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken); // succeeds

        failures.RemainingFailures = 2;
        await SaveAsync(Probe.Create(clock, "dumper"));
        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken); // retried
        clock.Advance(TimeSpan.FromSeconds(2));
        await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken); // dead-lettered

        var outcomes = measurements
            .Where(m => m.Instrument == "karry.outbox.messages")
            .Select(m => ((string)m.Tags["module"]!, (string)m.Tags["outcome"]!))
            .ToList();
        outcomes.Should().BeEquivalentTo(
        [
            ("probe", "retried"), ("probe", "processed"), ("probe", "retried"), ("probe", "dead_lettered"),
        ]);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-OPS-003")]
    public async Task Dispatch_and_database_spans_are_allow_listed_and_never_carry_parameter_values()
    {
        var spans = new List<Activity>();
        using (var tracing = Sdk.CreateTracerProviderBuilder()
            .AddNpgsql()
            .AddSource(ObservabilityExtensions.KarrySources)
            .AddProcessor(new AttributeAllowListProcessor())
            .AddInMemoryExporter(spans)
            .Build())
        {
            await SaveAsync(Probe.Create(clock, "Jane Doe jane.doe@example.com")); // the name is a SQL parameter
            await Dispatcher.DispatchPendingAsync(10, TestContext.Current.CancellationToken);
        }

        var dispatch = spans.Should().ContainSingle(s => s.DisplayName == "outbox dispatch").Subject;
        dispatch.GetTagItem(TelemetryAttributes.Module).Should().Be("probe");
        dispatch.GetTagItem(TelemetryAttributes.EventType).Should().Be("probe.renamed.v1");
        var database = spans.Where(s => s.Source.Name == "Npgsql").ToList();
        database.Should().Contain(s => ((string?)s.GetTagItem("db.query.text") ?? string.Empty).Contains("INSERT INTO probe.probes", StringComparison.Ordinal),
            "the SQL text is kept for diagnosis");
        foreach (var span in spans)
        {
            span.TagObjects.Should().OnlyContain(t => TelemetryAttributes.Allowed.Contains(t.Key));
            span.TagObjects.Select(t => t.Value?.ToString() ?? string.Empty).Should().NotContain(v => v.Contains("Jane", StringComparison.Ordinal) || v.Contains("jane.doe", StringComparison.Ordinal));
        }
    }

    private double OldestPendingAge()
    {
        measurements.Clear();
        listener.RecordObservableInstruments();
        return measurements
            .Where(m => m.Instrument == "karry.outbox.oldest_pending_age" && Equals(m.Tags["module"], "probe"))
            .Select(m => m.Value)
            .Should().ContainSingle().Subject;
    }

    private void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        measurements.Enqueue((instrument.Name, value, copy));
    }

    private async Task SaveAsync(Probe probe)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProbeDbContext>();
        db.Probes.Add(probe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
