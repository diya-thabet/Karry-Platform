using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Flags.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Karry.Modules.Flags.Infrastructure;

/// <summary>
/// Schema <c>flags</c>: platform-wide values and their append-only history (platform data: no tenant_id, no RLS; see
/// global-reference-tables) and company/user overrides (tenant-owned, row-level security). ADR-0019.
/// </summary>
public sealed class FlagsDbContext(DbContextOptions<FlagsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "flags";

    public override string Schema => SchemaName;

    public DbSet<PlatformFlag> PlatformFlags => Set<PlatformFlag>();

    public DbSet<PlatformFlagChange> PlatformFlagChanges => Set<PlatformFlagChange>();

    public DbSet<TenantFlag> TenantFlags => Set<TenantFlag>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<PlatformFlag>(flag =>
        {
            flag.ToTable("platform_flags");
            flag.HasKey(f => f.Key);
            flag.Property(f => f.Key).HasMaxLength(100);
            flag.Property(f => f.ChangedBy).HasMaxLength(255);
        });

        modelBuilder.Entity<PlatformFlagChange>(change =>
        {
            change.ToTable("platform_flag_changes");
            change.HasKey(c => c.Id);
            change.Property(c => c.Id).ValueGeneratedNever();
            change.Property(c => c.Key).HasMaxLength(100);
            change.Property(c => c.Actor).HasMaxLength(255);
            change.HasIndex(c => new { c.Key, c.At });
        });

        modelBuilder.Entity<TenantFlag>(flag =>
        {
            flag.ToTable("tenant_flags");
            flag.HasKey(f => f.Id);
            flag.Property(f => f.Id).HasConversion(id => id.Value, value => new TenantFlagId(value)).ValueGeneratedNever();
            flag.Property(f => f.Key).HasMaxLength(100);
            flag.Property(f => f.Subject).HasMaxLength(TenantFlag.SubjectMaxLength);
            flag.HasIndex(f => new { f.TenantId, f.Key, f.Subject }).IsUnique();
            flag.Ignore(f => f.DomainEvents);
            flag.Ignore(f => f.AggregateKey);
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class FlagsDbContextFactory : IDesignTimeDbContextFactory<FlagsDbContext>
{
    public FlagsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FlagsDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
