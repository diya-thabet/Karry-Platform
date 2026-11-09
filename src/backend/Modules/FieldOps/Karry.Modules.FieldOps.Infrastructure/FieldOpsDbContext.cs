using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Karry.Modules.FieldOps.Infrastructure;

/// <summary>
/// Schema <c>fieldops</c> (tenant-owned, RLS): shift entries as append-only versions (ADR-0034 §3). Meter values are
/// <c>numeric(9,1)</c>, as in Assets, next to the meter's kind.
/// </summary>
public sealed class FieldOpsDbContext(DbContextOptions<FieldOpsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "fieldops";

    public override string Schema => SchemaName;

    public DbSet<ShiftEntryVersion> ShiftEntries => Set<ShiftEntryVersion>();

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
            entry.Property(e => e.StartPhoto).HasMaxLength(71);
            entry.Property(e => e.EndPhoto).HasMaxLength(71);
            entry.Property(e => e.Remarks).HasMaxLength(ShiftEntryRules.MaxRemarks);
            entry.Property(e => e.Stoppages).HasColumnType("jsonb").HasConversion(Json<Stoppage>.Converter, Json<Stoppage>.Comparer);
            entry.Property(e => e.Flags).HasColumnType("text[]").HasConversion(
                new ValueConverter<IReadOnlyList<string>, string[]>(flags => flags.ToArray(), array => array),
                new ValueComparer<IReadOnlyList<string>>((a, b) => a!.SequenceEqual(b!), flags => string.Join(',', flags).GetHashCode(StringComparison.Ordinal), flags => flags.ToArray()));
        });
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
