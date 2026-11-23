using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Verification.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Karry.Modules.Verification.Infrastructure;

/// <summary>
/// Schema <c>verification</c> (tenant-owned, RLS): review items, one per record version (ADR-0037 §2). An item is never
/// deleted; its state changes only through ledgered decisions.
/// </summary>
public sealed class VerificationDbContext(DbContextOptions<VerificationDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "verification";

    public override string Schema => SchemaName;

    public DbSet<ReviewItem> Items => Set<ReviewItem>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ReviewItem>(item =>
        {
            item.ToTable("review_items", t =>
            {
                t.HasCheckConstraint("ck_review_items_status", $"status IN ({string.Join(", ", ReviewStatuses.All.Select(s => $"'{s}'"))})");
                t.HasCheckConstraint("ck_review_items_version", "record_version >= 1");
            });
            item.HasKey(i => i.Id);
            item.Ignore(i => i.DomainEvents);
            item.Property(i => i.Id).HasConversion(id => id.Value, value => new ReviewItemId(value)).ValueGeneratedNever();
            item.Property(i => i.RecordType).HasMaxLength(60);
            item.Property(i => i.RecordHash).HasMaxLength(80);
            item.Property(i => i.Status).HasMaxLength(20);
            item.Property(i => i.Flags).HasColumnType("jsonb").HasConversion(JsonList.Converter, JsonList.Comparer);
            item.Property(i => i.Version).IsRecordVersion();
            // One item per record version: a version submitted again (an upload retried) changes nothing.
            item.HasIndex(i => new { i.TenantId, i.RecordType, i.RecordId, i.RecordVersion }).IsUnique();
            item.HasIndex(i => new { i.TenantId, i.SiteId, i.Status, i.CreatedAt });
        });
    }

    private static class JsonList
    {
        public static readonly ValueConverter<IReadOnlyList<string>, string> Converter = new(
            value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
            json => JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonSerializerOptions.Web)!);

        public static readonly ValueComparer<IReadOnlyList<string>> Comparer = new(
            (a, b) => a!.SequenceEqual(b!),
            value => string.Join('|', value).GetHashCode(StringComparison.Ordinal),
            value => value.ToList());
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class VerificationDbContextFactory : IDesignTimeDbContextFactory<VerificationDbContext>
{
    public VerificationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<VerificationDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
