using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;

namespace Karry.BuildingBlocks.IntegrationTests;

// A minimal module used only by tests: an aggregate, its events, a projection written by a subscriber.

public readonly record struct ProbeId(Guid Value) : IEntityId<ProbeId>
{
    public static ProbeId From(Guid value) => new(value);
}

[EventType("probe.renamed.v1")]
[AuditSubject("probe", nameof(ProbeId))]
public sealed record ProbeRenamed(Guid ProbeId, string Name, int Sequence) : DomainEvent;

public sealed class Probe : AggregateRoot<ProbeId>
{
    private Probe(ProbeId id)
        : base(id)
    {
    }

    public string Name { get; private set; } = string.Empty;

    public int Version { get; private set; }

    public static Probe Create(TimeProvider clock, string name)
    {
        var probe = new Probe(EntityId.New<ProbeId>(clock));
        probe.Rename(clock, name);
        return probe;
    }

    public void Rename(TimeProvider clock, string name)
    {
        Name = name;
        Version++;
        Raise(new ProbeRenamed(Id.Value, name, Version) { OccurredAt = clock.GetUtcNow() });
    }
}

/// <summary>Projection row written by <see cref="LogSubscriber"/> in the dispatch transaction.</summary>
public sealed class ProbeLog
{
    public long Id { get; init; }

    public Guid MessageId { get; init; }

    public Guid ProbeId { get; init; }

    public int Sequence { get; init; }
}

public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : ModuleDbContext(options)
{
    public override string Schema => "probe";

    public DbSet<Probe> Probes => Set<Probe>();

    public DbSet<ProbeLog> Logs => Set<ProbeLog>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Probe>(probe =>
        {
            probe.HasKey(p => p.Id);
            probe.Property(p => p.Id).HasConversion(id => id.Value, value => ProbeId.From(value));
            probe.Ignore(p => p.DomainEvents);
            probe.Ignore(p => p.AggregateKey);
        });
        modelBuilder.Entity<ProbeLog>(log =>
        {
            log.HasKey(l => l.Id);
            log.Property(l => l.Id).UseIdentityAlwaysColumn();
        });
    }
}

public sealed class LogSubscriber : IDomainEventSubscriber<ProbeRenamed>
{
    public Task HandleAsync(ProbeRenamed domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        context.Db.Set<ProbeLog>().Add(new ProbeLog { MessageId = context.MessageId, ProbeId = domainEvent.ProbeId, Sequence = domainEvent.Sequence });
        return Task.CompletedTask;
    }
}

/// <summary>Fails while <see cref="FailureSwitch.RemainingFailures"/> &gt; 0 (thread-safe), then succeeds.</summary>
public sealed class FlakySubscriber(FailureSwitch failures) : IDomainEventSubscriber<ProbeRenamed>
{
    public Task HandleAsync(ProbeRenamed domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        if (failures.ShouldFail())
        {
            throw new InvalidOperationException("Simulated downstream outage");
        }

        return Task.CompletedTask;
    }
}

public sealed class FailureSwitch
{
    private int remainingFailures;

    public int RemainingFailures
    {
        get => Volatile.Read(ref remainingFailures);
        set => Volatile.Write(ref remainingFailures, value);
    }

    /// <summary>True while a simulated failure is still due (thread-safe countdown).</summary>
    public bool ShouldFail() => Interlocked.Decrement(ref remainingFailures) >= 0;
}
