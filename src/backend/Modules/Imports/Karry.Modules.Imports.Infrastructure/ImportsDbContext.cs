using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Imports.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Karry.Modules.Imports.Infrastructure;

/// <summary>
/// Schema <c>imports</c> (tenant-owned, RLS): the import jobs, and the issues and preview of each job's last validation
/// (replaced at each validation) (ADR-0027).
/// </summary>
public sealed class ImportsDbContext(DbContextOptions<ImportsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "imports";

    private static readonly ValueConverter<IReadOnlyDictionary<string, string>?, string> ArgsConverter = new(
        args => JsonSerializer.Serialize(args, JsonSerializerOptions.Default),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonSerializerOptions.Default)!);

    // Issues are written once and never changed: comparing by reference is enough.
    private static readonly ValueComparer<IReadOnlyDictionary<string, string>?> ArgsComparer = new(
        (a, b) => ReferenceEquals(a, b), args => args == null ? 0 : args.Count, args => args);

    public override string Schema => SchemaName;

    public DbSet<ImportJob> Jobs => Set<ImportJob>();

    public DbSet<ImportJobIssue> Issues => Set<ImportJobIssue>();

    public DbSet<ImportJobRow> Rows => Set<ImportJobRow>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ImportJob>(job =>
        {
            job.ToTable("jobs");
            job.HasKey(j => j.Id);
            job.Property(j => j.Id).HasConversion(id => id.Value, value => new ImportJobId(value)).ValueGeneratedNever();
            job.Property(j => j.Kind).HasMaxLength(30);
            job.Property(j => j.FileName).HasMaxLength(ImportJob.FileNameMaxLength);
            job.Property(j => j.Sha256).HasMaxLength(64).IsFixedLength();
            job.Property(j => j.RequestedBy).HasMaxLength(255);
            job.Property(j => j.CommitRequestedBy).HasMaxLength(255);
            job.Property(j => j.Scanner).HasMaxLength(50);
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            job.HasIndex(j => new { j.TenantId, j.UploadedAt });

            // A commit request and the worker's result on the same job at once: the second save fails and is retried.
            job.Property<uint>("xmin").IsRowVersion();
            job.Ignore(j => j.IsQueued);
            job.Ignore(j => j.DomainEvents);
            job.Ignore(j => j.AggregateKey);
        });

        modelBuilder.Entity<ImportJobIssue>(issue =>
        {
            issue.ToTable("issues");
            issue.Property<long>("Id").UseIdentityAlwaysColumn();
            issue.HasKey("Id");
            issue.Property(i => i.JobId).HasConversion(id => id.Value, value => new ImportJobId(value));
            issue.HasOne<ImportJob>().WithMany().HasForeignKey(i => i.JobId).OnDelete(DeleteBehavior.Cascade);
            issue.Property(i => i.Column).HasMaxLength(60);
            issue.Property(i => i.Cell).HasMaxLength(20);
            issue.Property(i => i.Code).HasMaxLength(100);
            issue.Property(i => i.Severity).HasMaxLength(10);
            issue.Property(i => i.Args).HasColumnType("jsonb").HasConversion(ArgsConverter, ArgsComparer);
            issue.HasIndex(i => new { i.JobId, i.Sequence }).IsUnique();
        });

        modelBuilder.Entity<ImportJobRow>(row =>
        {
            row.ToTable("rows");
            row.Property<long>("Id").UseIdentityAlwaysColumn();
            row.HasKey("Id");
            row.Property(r => r.JobId).HasConversion(id => id.Value, value => new ImportJobId(value));
            row.HasOne<ImportJob>().WithMany().HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Cascade);
            row.Property(r => r.Key).HasMaxLength(200);
            row.Property(r => r.Action).HasMaxLength(10);
            row.HasIndex(r => new { r.JobId, r.Row }).IsUnique();
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class ImportsDbContextFactory : IDesignTimeDbContextFactory<ImportsDbContext>
{
    public ImportsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ImportsDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
