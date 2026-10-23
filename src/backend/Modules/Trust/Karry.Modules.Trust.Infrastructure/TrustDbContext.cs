using Karry.BuildingBlocks.Persistence;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>A row of <c>audit.ledger_entries</c> (append-only: triggers reject UPDATE/DELETE/TRUNCATE).</summary>
public sealed class LedgerEntryRow : ITenantOwned
{
    public TenantId TenantId { get; init; }

    public long Seq { get; init; }

    public Guid EventId { get; init; }

    public required string EventType { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>RFC 8785 canonical JSON, stored as text so the exact hashed bytes are kept.</summary>
    public required string Payload { get; init; }

    public required string PayloadHash { get; init; }

    public required string PrevHash { get; init; }

    public required string EntryHash { get; init; }

    public DateTimeOffset RecordedAt { get; init; }
}

/// <summary>The chain head of a tenant: last sequence and hash; locked FOR UPDATE to append gap-free.</summary>
public sealed class LedgerHeadRow : ITenantOwned
{
    public TenantId TenantId { get; init; }

    public long LastSeq { get; init; }

    public required string LastHash { get; init; }
}

/// <summary>Schema <c>audit</c>: the per-tenant hash-chained ledger (ADR-0008).</summary>
public sealed class TrustDbContext(DbContextOptions<TrustDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "audit";

    public override string Schema => SchemaName;

    public DbSet<LedgerEntryRow> LedgerEntries => Set<LedgerEntryRow>();

    public DbSet<LedgerHeadRow> LedgerHeads => Set<LedgerHeadRow>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<LedgerEntryRow>(entry =>
        {
            entry.ToTable("ledger_entries");
            entry.HasKey(e => new { e.TenantId, e.Seq });
            entry.HasIndex(e => e.EventId).IsUnique();
            entry.Property(e => e.EventType).HasMaxLength(200);
            entry.Property(e => e.PayloadHash).HasMaxLength(64).IsFixedLength();
            entry.Property(e => e.PrevHash).HasMaxLength(64).IsFixedLength();
            entry.Property(e => e.EntryHash).HasMaxLength(64).IsFixedLength();
            entry.Property(e => e.RecordedAt).HasDefaultValueSql("now()");
        });
        modelBuilder.Entity<LedgerHeadRow>(head =>
        {
            head.ToTable("ledger_heads");
            head.HasKey(h => h.TenantId);
            head.Property(h => h.LastHash).HasMaxLength(64).IsFixedLength();
        });
    }
}
