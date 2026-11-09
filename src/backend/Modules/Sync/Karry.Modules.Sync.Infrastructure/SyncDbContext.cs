using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Sync.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Karry.Modules.Sync.Infrastructure;

/// <summary>
/// Schema <c>sync</c> (tenant-owned, RLS): the inbox of commands uploaded by tablets (ADR-0028 §6). A device's
/// received commands hold each sequence number once; quarantined ones are outside the sequence. Each received command
/// has its handler's outcome in <c>processed_commands</c>; commands of a type no module handles are kept in
/// <c>unhandled_commands</c> (ADR-0031). All three are append-only.
/// </summary>
public sealed class SyncDbContext(DbContextOptions<SyncDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "sync";

    public override string Schema => SchemaName;

    public DbSet<InboxCommand> Inbox => Set<InboxCommand>();

    public DbSet<ProcessedCommand> Processed => Set<ProcessedCommand>();

    public DbSet<UnhandledCommand> Unhandled => Set<UnhandledCommand>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<InboxCommand>(command =>
        {
            command.ToTable("inbox");
            command.HasKey(c => new { c.DeviceId, c.Id });
            command.Property(c => c.Id).HasConversion(id => id.Value, value => new CommandId(value)).ValueGeneratedNever();
            command.Property(c => c.Type).HasMaxLength(InboxRules.TypeMaxLength);
            command.Property(c => c.Signature).HasMaxLength(100);
            command.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            command.HasIndex(c => new { c.DeviceId, c.Sequence }).IsUnique().HasFilter("status = 'Received'");
        });

        modelBuilder.Entity<ProcessedCommand>(processed =>
        {
            processed.ToTable("processed_commands");
            processed.HasKey(p => new { p.DeviceId, p.CommandId });
            processed.Property(p => p.CommandId).HasConversion(id => id.Value, value => new CommandId(value)).ValueGeneratedNever();
            processed.Property(p => p.Outcome).HasConversion<string>().HasMaxLength(20);
            processed.Property(p => p.Flags).HasColumnType("text[]");
            processed.HasOne<InboxCommand>().WithOne().HasForeignKey<ProcessedCommand>(p => new { p.DeviceId, p.CommandId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UnhandledCommand>(unhandled =>
        {
            unhandled.ToTable("unhandled_commands");
            unhandled.HasKey(c => new { c.DeviceId, c.Id });
            unhandled.Property(c => c.Id).HasConversion(id => id.Value, value => new CommandId(value)).ValueGeneratedNever();
            unhandled.Property(c => c.Type).HasMaxLength(InboxRules.TypeMaxLength);
            unhandled.Property(c => c.Signature).HasMaxLength(100);
            unhandled.Property(c => c.Reason).HasMaxLength(60);
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class SyncDbContextFactory : IDesignTimeDbContextFactory<SyncDbContext>
{
    public SyncDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SyncDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
