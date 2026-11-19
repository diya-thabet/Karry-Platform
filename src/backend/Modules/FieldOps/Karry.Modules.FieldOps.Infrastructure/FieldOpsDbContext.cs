using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Karry.Modules.FieldOps.Infrastructure;

/// <summary>
/// Schema <c>fieldops</c> (tenant-owned, RLS): shift entries as append-only versions (ADR-0034 §3); checklist templates,
/// submissions and safety holds, append-only too (ADR-0035). Meter values are
/// <c>numeric(9,1)</c>, as in Assets, next to the meter's kind.
/// </summary>
public sealed class FieldOpsDbContext(DbContextOptions<FieldOpsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "fieldops";

    public override string Schema => SchemaName;

    public DbSet<ShiftEntryVersion> ShiftEntries => Set<ShiftEntryVersion>();

    public DbSet<ChecklistTemplate> ChecklistTemplates => Set<ChecklistTemplate>();

    public DbSet<ChecklistSubmission> ChecklistSubmissions => Set<ChecklistSubmission>();

    public DbSet<SafetyHold> SafetyHolds => Set<SafetyHold>();

    public DbSet<SafetyHoldRelease> SafetyHoldReleases => Set<SafetyHoldRelease>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ShiftEntryVersion>(entry =>
        {
            entry.ToTable("shift_entries", t =>
            {
                t.HasCheckConstraint("ck_shift_entries_kind", "meter_kind IN ('hours', 'km')");
                t.HasCheckConstraint("ck_shift_entries_values", "start_value >= 0 AND (end_value IS NULL OR end_value >= 0)");
                t.HasCheckConstraint("ck_shift_entries_fuel", "fuel_start_percent BETWEEN 0 AND 100 AND (fuel_end_percent IS NULL OR fuel_end_percent BETWEEN 0 AND 100)");
                t.HasCheckConstraint("ck_shift_entries_positions",
                    "(start_latitude IS NULL) = (start_longitude IS NULL) AND (start_latitude IS NULL) = (start_accuracy_m IS NULL)"
                    + " AND (end_latitude IS NULL) = (end_longitude IS NULL) AND (end_latitude IS NULL) = (end_accuracy_m IS NULL)"
                    + " AND coalesce(abs(start_latitude) <= 90 AND abs(start_longitude) <= 180 AND start_accuracy_m >= 0, true)"
                    + " AND coalesce(abs(end_latitude) <= 90 AND abs(end_longitude) <= 180 AND end_accuracy_m >= 0, true)");
                t.HasCheckConstraint("ck_shift_entries_closed", "(status = 'Closed') = (ended_at IS NOT NULL AND end_value IS NOT NULL AND end_photo IS NOT NULL)");
            });
            entry.HasKey(e => new { e.TenantId, e.RootId, e.Version });
            entry.HasIndex(e => new { e.TenantId, e.MeterId, e.StartedAt });
            entry.HasIndex(e => new { e.TenantId, e.OperatorId, e.StartedAt });
            entry.HasIndex(e => new { e.TenantId, e.SiteId, e.ShiftDate });
            entry.HasIndex(e => e.CommandId).IsUnique();
            entry.Property(e => e.Status).HasConversion<string>().HasMaxLength(10);
            entry.Property(e => e.MeterKind).HasMaxLength(10);
            entry.Property(e => e.ShiftCode).HasMaxLength(20);
            entry.Property(e => e.StartValue).HasPrecision(9, 1);
            entry.Property(e => e.EndValue).HasPrecision(9, 1);
            Position(entry.ComplexProperty(e => e.StartPosition), "start");
            Position(entry.ComplexProperty(e => e.EndPosition), "end");
            entry.Property(e => e.StartPhoto).HasMaxLength(71);
            entry.Property(e => e.EndPhoto).HasMaxLength(71);
            entry.Property(e => e.Remarks).HasMaxLength(ShiftEntryRules.MaxRemarks);
            entry.Property(e => e.Stoppages).HasColumnType("jsonb").HasConversion(Json<Stoppage>.Converter, Json<Stoppage>.Comparer);
            entry.Property(e => e.Flags).HasColumnType("text[]").HasConversion(Codes.Converter, Codes.Comparer);
        });

        // Pre-start checklists (ADR-0035 §2): one row per published version, never changed; one number per asset type.
        modelBuilder.Entity<ChecklistTemplate>(template =>
        {
            template.ToTable("checklist_templates", t => t.HasCheckConstraint("ck_checklist_templates_version", "version >= 1"));
            template.HasKey(c => c.Id);
            template.Ignore(c => c.DomainEvents);
            template.Property(c => c.Id).HasConversion(id => id.Value, value => new ChecklistTemplateId(value)).ValueGeneratedNever();
            template.HasIndex(c => new { c.TenantId, c.AssetType, c.Version }).IsUnique();
            template.Property(c => c.AssetType).HasMaxLength(40);
            template.Property(c => c.PublishedBy).HasMaxLength(200);
            template.Property(c => c.Items).HasColumnType("jsonb").HasConversion(Json<ChecklistItem>.Converter, Json<ChecklistItem>.Comparer);
        });

        // Signed checklists (ADR-0035 §3): one row each, never changed; the answers as signed.
        modelBuilder.Entity<ChecklistSubmission>(submission =>
        {
            submission.ToTable("checklist_submissions", t => t.HasCheckConstraint("ck_checklist_submissions_version", "template_version >= 1"));
            submission.HasKey(s => new { s.TenantId, s.RootId });
            submission.HasIndex(s => new { s.TenantId, s.AssetId, s.SubmittedAt });
            submission.HasIndex(s => s.CommandId).IsUnique();
            submission.Property(s => s.TemplateId).HasConversion(id => id.Value, value => new ChecklistTemplateId(value));
            submission.Property(s => s.AssetType).HasMaxLength(40);
            submission.Property(s => s.Answers).HasColumnType("jsonb").HasConversion(Json<ChecklistAnswer>.Converter, Json<ChecklistAnswer>.Comparer);
            submission.Property(s => s.CriticalItems).HasColumnType("text[]").HasConversion(Codes.Converter, Codes.Comparer);
        });

        // Safety holds (ADR-0035 §4, §5): opened by a submission, ended by a release with the hold's ID; neither changes.
        modelBuilder.Entity<SafetyHold>(hold =>
        {
            hold.ToTable("safety_holds");
            hold.HasKey(h => h.Id);
            hold.Ignore(h => h.DomainEvents);
            hold.Property(h => h.Id).HasConversion(id => id.Value, value => new SafetyHoldId(value)).ValueGeneratedNever();
            hold.HasIndex(h => new { h.TenantId, h.AssetId, h.OpenedAt });
            hold.HasIndex(h => new { h.TenantId, h.SubmissionId }).IsUnique();
            hold.Property(h => h.Items).HasColumnType("text[]").HasConversion(Codes.Converter, Codes.Comparer);
        });
        modelBuilder.Entity<SafetyHoldRelease>(release =>
        {
            release.ToTable("safety_hold_releases", t => t.HasCheckConstraint("ck_safety_hold_releases_reason", "char_length(reason) BETWEEN 10 AND 500"));
            release.HasKey(r => r.Id);
            release.Ignore(r => r.DomainEvents);
            release.Property(r => r.Id).HasConversion(id => id.Value, value => new SafetyHoldId(value)).ValueGeneratedNever();
            release.HasOne<SafetyHold>().WithOne().HasForeignKey<SafetyHoldRelease>(r => r.Id).OnDelete(DeleteBehavior.Restrict);
            release.Property(r => r.ReleasedBy).HasMaxLength(200);
            release.Property(r => r.Reason).HasMaxLength(SafetyHoldRelease.ReasonMaxLength);
        });
    }

    /// <summary>A position as three columns, <c>{side}_latitude</c>, <c>{side}_longitude</c>, <c>{side}_accuracy_m</c>.</summary>
    private static void Position(ComplexPropertyBuilder<Position> position, string side)
    {
        position.Property(p => p.Latitude).HasColumnName($"{side}_latitude").HasPrecision(8, 6);
        position.Property(p => p.Longitude).HasColumnName($"{side}_longitude").HasPrecision(9, 6);
        position.Property(p => p.AccuracyM).HasColumnName($"{side}_accuracy_m");
    }

    /// <summary>A list of codes as a <c>text[]</c> column.</summary>
    private static class Codes
    {
        public static readonly ValueConverter<IReadOnlyList<string>, string[]> Converter = new(codes => codes.ToArray(), array => array);

        public static readonly ValueComparer<IReadOnlyList<string>> Comparer = new(
            (a, b) => a!.SequenceEqual(b!), codes => string.Join(',', codes).GetHashCode(StringComparison.Ordinal), codes => codes.ToArray());
    }

    private static class Json<T>
    {
        public static readonly ValueConverter<IReadOnlyList<T>, string> Converter = new(
            items => JsonSerializer.Serialize(items, JsonSerializerOptions.Web), json => JsonSerializer.Deserialize<List<T>>(json, JsonSerializerOptions.Web) ?? new List<T>());

        public static readonly ValueComparer<IReadOnlyList<T>> Comparer = new(
            (a, b) => a!.SequenceEqual(b!), items => items.Count, items => items.ToList());
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class FieldOpsDbContextFactory : IDesignTimeDbContextFactory<FieldOpsDbContext>
{
    public FieldOpsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FieldOpsDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
