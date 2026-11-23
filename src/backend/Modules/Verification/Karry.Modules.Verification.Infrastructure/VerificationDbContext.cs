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

    public DbSet<ReviewAction> Actions => Set<ReviewAction>();

    public DbSet<ReviewBatch> Batches => Set<ReviewBatch>();

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
            item.Property(i => i.CorrectedBy).HasMaxLength(200);
            item.Property(i => i.Flags).HasColumnType("jsonb").HasConversion(JsonList.Converter, JsonList.Comparer);
            item.Property(i => i.Version).IsRecordVersion();
            // One item per record version: a version submitted again (an upload retried) changes nothing.
            item.HasIndex(i => new { i.TenantId, i.RecordType, i.RecordId, i.RecordVersion }).IsUnique();
            item.HasIndex(i => new { i.TenantId, i.SiteId, i.Status, i.CreatedAt });
        });

        modelBuilder.Entity<ReviewAction>(action =>
        {
            action.ToTable("review_actions", t =>
            {
                t.HasCheckConstraint("ck_review_actions_kind", $"kind IN ({string.Join(", ", ReviewActionKinds.All.Select(k => $"'{k}'"))})");
                t.HasCheckConstraint("ck_review_actions_reason", $"reason_code IS NULL OR reason_code IN ({string.Join(", ", RejectionReasons.All.Select(r => $"'{r}'"))})");
            });
            action.HasKey(a => a.Id);
            action.Property(a => a.Id).HasConversion(id => id.Value, value => new ReviewActionId(value)).ValueGeneratedNever();
            action.Property(a => a.ItemId).HasConversion(id => id.Value, value => new ReviewItemId(value));
            action.Property(a => a.Kind).HasMaxLength(20);
            action.Property(a => a.Actor).HasMaxLength(200);
            action.Property(a => a.ReasonCode).HasMaxLength(30);
            action.Property(a => a.Comment).HasMaxLength(ReviewRules.CommentMaxLength);
            action.Property(a => a.RecordHash).HasMaxLength(80);
            action.Property(a => a.BatchId).HasConversion(new ValueConverter<ReviewBatchId, Guid>(id => id.Value, value => new ReviewBatchId(value)));
            action.HasIndex(a => new { a.TenantId, a.ItemId, a.At });
        });

        modelBuilder.Entity<ReviewBatch>(batch =>
        {
            batch.ToTable("review_batches");
            batch.HasKey(b => b.Id);
            batch.Ignore(b => b.DomainEvents);
            batch.Property(b => b.Id).HasConversion(id => id.Value, value => new ReviewBatchId(value)).ValueGeneratedNever();
            batch.Property(b => b.Actor).HasMaxLength(200);
            batch.Property(b => b.ItemIds).HasColumnType("jsonb").HasConversion(JsonGuids.Converter, JsonGuids.Comparer);
            batch.Property(b => b.RecordHashes).HasColumnType("jsonb").HasConversion(JsonList.Converter, JsonList.Comparer);
            batch.HasIndex(b => new { b.TenantId, b.At });
        });
    }

    private static class JsonGuids
    {
        public static readonly ValueConverter<IReadOnlyList<Guid>, string> Converter = new(
            value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
            json => JsonSerializer.Deserialize<IReadOnlyList<Guid>>(json, JsonSerializerOptions.Web)!);

        public static readonly ValueComparer<IReadOnlyList<Guid>> Comparer = new(
            (a, b) => a!.SequenceEqual(b!),
            value => value.Count,
            value => value.ToList());
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
