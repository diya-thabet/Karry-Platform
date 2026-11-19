using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Integrity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Karry.Modules.Integrity.Infrastructure;

/// <summary>
/// Schema <c>integrity</c> (tenant-owned, RLS): flags (ADR-0036 §2) and the company's rule settings (§1). A flag row
/// changes once, from open to closed; it is never deleted.
/// </summary>
public sealed class IntegrityDbContext(DbContextOptions<IntegrityDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "integrity";

    public override string Schema => SchemaName;

    public DbSet<IntegrityFlag> Flags => Set<IntegrityFlag>();

    public DbSet<RuleSetting> RuleSettings => Set<RuleSetting>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<IntegrityFlag>(flag =>
        {
            flag.ToTable("flags", t =>
            {
                t.HasCheckConstraint("ck_flags_severity", "severity IN ('low', 'medium', 'high', 'critical')");
                t.HasCheckConstraint("ck_flags_closed", "(closed_at IS NULL) = (close_reason IS NULL)");
            });
            flag.HasKey(f => f.Id);
            flag.Ignore(f => f.DomainEvents);
            flag.Ignore(f => f.IsOpen);
            flag.Property(f => f.Id).HasConversion(id => id.Value, value => new IntegrityFlagId(value)).ValueGeneratedNever();
            flag.Property(f => f.Rule).HasMaxLength(20);
            flag.Property(f => f.Severity).HasMaxLength(10);
            flag.Property(f => f.SubjectType).HasMaxLength(40);
            flag.Property(f => f.Key).HasMaxLength(100);
            flag.Property(f => f.CloseReason).HasMaxLength(100);
            flag.Property(f => f.ClosedBy).HasMaxLength(200);
            flag.Property(f => f.Related).HasColumnType("jsonb").HasConversion(Json<IReadOnlyList<SubjectRef>>.Converter, Json<IReadOnlyList<SubjectRef>>.Comparer);
            flag.Property(f => f.Evidence).HasColumnType("jsonb").HasConversion(Json<IReadOnlyList<string>>.Converter, Json<IReadOnlyList<string>>.Comparer);
            flag.Property(f => f.Details).HasColumnType("jsonb").HasConversion(Json<IReadOnlyDictionary<string, string>>.Converter, Json<IReadOnlyDictionary<string, string>>.Comparer);
            // One open flag per subject, rule and key; closed ones accumulate.
            flag.HasIndex(f => new { f.TenantId, f.SubjectType, f.SubjectId, f.Rule, f.Key }).IsUnique().HasFilter("closed_at IS NULL");
            flag.HasIndex(f => new { f.TenantId, f.SiteId, f.OpenedAt });
        });

        modelBuilder.Entity<RuleSetting>(setting =>
        {
            setting.ToTable("rule_settings", t =>
                t.HasCheckConstraint("ck_rule_settings_security_on", "enabled OR rule NOT IN ('RULE-FO-05', 'RULE-FO-06', 'RULE-FO-07')"));
            setting.HasKey(s => s.Id);
            setting.Ignore(s => s.DomainEvents);
            setting.Property(s => s.Id).HasConversion(id => id.Value, value => new RuleSettingId(value)).ValueGeneratedNever();
            setting.Property(s => s.Rule).HasMaxLength(20);
            setting.Property(s => s.ChangedBy).HasMaxLength(200);
            setting.Property(s => s.Thresholds).HasColumnType("jsonb")
                .HasConversion(Json<IReadOnlyDictionary<string, decimal>>.Converter, Json<IReadOnlyDictionary<string, decimal>>.Comparer);
            setting.Property(s => s.Version).IsRecordVersion();
            setting.HasIndex(s => new { s.TenantId, s.Rule }).IsUnique(); // one row per rule and company
        });
    }

    private static class Json<T>
        where T : class
    {
        public static readonly ValueConverter<T, string> Converter = new(
            value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web), json => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!);

        public static readonly ValueComparer<T> Comparer = new(
            (a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Web) == JsonSerializer.Serialize(b, JsonSerializerOptions.Web),
            value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web).GetHashCode(StringComparison.Ordinal),
            value => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!);
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class IntegrityDbContextFactory : IDesignTimeDbContextFactory<IntegrityDbContext>
{
    public IntegrityDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
