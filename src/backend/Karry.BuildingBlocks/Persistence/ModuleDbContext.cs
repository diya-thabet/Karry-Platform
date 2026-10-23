using System.Linq.Expressions;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Karry.BuildingBlocks.Persistence;

/// <summary>
/// Base DbContext of a module: its own PostgreSQL schema, snake_case names, and the unit of work. Saving writes the
/// business changes and the outbox rows for every raised domain event in <b>one transaction</b> (ADR-0015); the
/// audit ledger entry joins the same transaction in P1.6.
/// </summary>
public abstract partial class ModuleDbContext : DbContext
{
    protected ModuleDbContext(DbContextOptions options)
        : base(options)
    {
        // Every SaveChanges runs in a transaction, also for a single statement: the tenant interceptor sets RLS once per
        // transaction for writes (see TenantCommandInterceptor), and changes + outbox rows commit together anyway.
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
    }

    /// <summary>The module's schema, e.g. <c>assets</c>. Lowercase letters and underscores only.</summary>
    public abstract string Schema { get; }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    /// <summary>
    /// Tenant of this unit of work, read by the query filter of every <see cref="ITenantOwned"/> entity (the second
    /// isolation layer after RLS, US-P1-TEN-02 AC4). Null without a tenant: tenant-owned queries then return nothing.
    /// </summary>
    protected Guid? CurrentTenantId => TenantContext?.Current?.Value;

    /// <summary>Used by the query filter: the current tenant, or the empty GUID (matches no tenant) when there is none.</summary>
    protected TenantId CurrentTenantFilter => new(CurrentTenantId ?? Guid.Empty);

    /// <summary>
    /// The unit of work's tenant context, from the scope that created this context (AddDbContext passes the scoped
    /// provider as application service provider). Null for contexts created outside DI (migrations).
    /// </summary>
    private ITenantContext? TenantContext =>
        tenantContext ??= this.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider?.GetService(typeof(ITenantContext)) as ITenantContext;

    private ITenantContext? tenantContext;

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync: database access is asynchronous in Karry.");

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // Third check on writes (after the query filter and RLS WITH CHECK): never save a row for another tenant.
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Entity.TenantId.Value != CurrentTenantId)
            {
                throw new InvalidOperationException(
                    $"{entry.Entity.GetType().Name} belongs to another tenant than the current unit of work; refusing to save.");
            }
        }

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
                    TenantId = (aggregate as ITenantOwned)?.TenantId.Value,
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

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType) && t.BaseType is null))
        {
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(TenantFilter(entityType.ClrType));
            modelBuilder.Entity(entityType.ClrType).Property(nameof(ITenantOwned.TenantId))
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<TenantId, Guid>(id => id.Value, value => new TenantId(value)));
            modelBuilder.Entity(entityType.ClrType).HasIndex(nameof(ITenantOwned.TenantId));
        }
    }

    /// <summary><c>e => e.TenantId == this.CurrentTenantFilter</c>; EF re-reads the context property for every query.</summary>
    private LambdaExpression TenantFilter(Type entityType)
    {
        var entity = Expression.Parameter(entityType, "e");
        var tenant = Expression.Property(entity, nameof(ITenantOwned.TenantId));
        var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantFilter));
        return Expression.Lambda(Expression.Equal(tenant, current), entity);
    }

    /// <summary>Configure the module's own entities here.</summary>
    protected abstract void OnModuleModelCreating(ModelBuilder modelBuilder);

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z_]*$")]
    internal static partial System.Text.RegularExpressions.Regex SchemaPattern();
}
