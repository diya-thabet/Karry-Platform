using Karry.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Karry.BuildingBlocks.Idempotency;

/// <summary>Cross-cutting platform tables (schema <c>platform</c>): idempotency records.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : ModuleDbContext(options)
{
    public override string Schema => "platform";

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.ToTable("idempotency_records");
            record.HasKey(r => new { r.Scope, r.Key });
            record.Property(r => r.Scope).HasMaxLength(200);
            record.Property(r => r.Key).HasMaxLength(IdempotencyOptions.MaxKeyLength);
            record.Property(r => r.RequestFingerprint).HasMaxLength(64);
            record.Property(r => r.ContentType).HasMaxLength(200);
            record.HasIndex(r => r.ExpiresAt);
        });
    }
}
