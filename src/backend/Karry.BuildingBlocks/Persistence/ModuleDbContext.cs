using Karry.BuildingBlocks.Outbox;
using Karry.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.BuildingBlocks.Persistence;

/// <summary>
/// Base DbContext of a module: its own PostgreSQL schema, snake_case names, and the unit of work. Saving writes the
/// business changes and the outbox rows for every raised domain event in <b>one transaction</b> (ADR-0015); the
/// audit ledger entry joins the same transaction in P1.6.
/// </summary>
public abstract partial class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>The module's schema, e.g. <c>assets</c>. Lowercase letters and underscores only.</summary>
    public abstract string Schema { get; }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync: database access is asynchronous in Karry.");

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var aggregates = ChangeTracker.Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                OutboxMessages.Add(new OutboxMessage
                {
                    Id = domainEvent.EventId,
                    Type = EventTypeAttribute.NameOf(domainEvent.GetType()),
                    Source = Schema,
                    AggregateKey = aggregate.AggregateKey,
                    OccurredAt = domainEvent.OccurredAt,
                    NextAttemptAt = domainEvent.OccurredAt,
                    Payload = EventSerializer.Serialize(domainEvent),
                });
            }
        }

        // One SaveChanges = one transaction (or the caller's explicit transaction): changes and outbox rows commit
        // or roll back together.
        var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        aggregates.ForEach(aggregate => aggregate.ClearDomainEvents());
        return written;
    }

    /// <summary>Name of the EF Core migrations history table, kept in the module's own schema.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        // Keeps the connection configured by AddModuleDbContext / the migration runner and adds the history table.
        optionsBuilder.UseNpgsql(npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, Schema));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        if (!SchemaPattern().IsMatch(Schema))
        {
            throw new InvalidOperationException($"Schema '{Schema}' must be lowercase letters and underscores.");
        }

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Id).ValueGeneratedNever();
            outbox.Property(m => m.Type).HasMaxLength(200);
            outbox.Property(m => m.Source).HasMaxLength(63);
            outbox.Property(m => m.AggregateKey).HasMaxLength(200);
            outbox.Property(m => m.Payload).HasColumnType("jsonb");
            outbox.Property(m => m.LastError).HasMaxLength(2000);
            // Pending messages in dispatch order; the partial index stays small because processed rows drop out.
            outbox.HasIndex(m => new { m.OccurredAt, m.Id }).HasFilter("processed_at IS NULL AND dead_lettered_at IS NULL");
            outbox.HasIndex(m => new { m.AggregateKey, m.OccurredAt }).HasFilter("processed_at IS NULL AND dead_lettered_at IS NULL");
        });

        modelBuilder.Entity<ProcessedMessage>(processed =>
        {
            processed.ToTable("processed_messages");
            processed.HasKey(p => new { p.MessageId, p.Handler });
            processed.Property(p => p.Handler).HasMaxLength(300);
        });

        OnModuleModelCreating(modelBuilder);
    }

    /// <summary>Configure the module's own entities here.</summary>
    protected abstract void OnModuleModelCreating(ModelBuilder modelBuilder);

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z_]*$")]
    internal static partial System.Text.RegularExpressions.Regex SchemaPattern();
}
