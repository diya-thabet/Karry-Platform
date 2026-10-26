using System.Globalization;
using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Materials.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Karry.Modules.Materials.Infrastructure;

/// <summary>
/// Schema <c>materials</c> (tenant-owned, RLS): the company's materials with their density versions as one JSON
/// document (a handful per material, always read and written whole with it; ADR-0025).
/// </summary>
public sealed class MaterialsDbContext(DbContextOptions<MaterialsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "materials";

    public override string Schema => SchemaName;

    public DbSet<Material> Materials => Set<Material>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Material>(material =>
        {
            material.ToTable("materials", t => t.HasCheckConstraint("ck_materials_archived", "(status = 'Archived') = (archived_at IS NOT NULL)"));
            material.HasKey(m => m.Id);
            material.Property(m => m.Id).HasConversion(id => id.Value, value => new MaterialId(value)).ValueGeneratedNever();
            material.Property(m => m.Code).HasMaxLength(MaterialRules.CodeMaxLength);
            material.HasIndex(m => new { m.TenantId, m.Code }).IsUnique();
            material.Property(m => m.Name).HasMaxLength(MaterialRules.NameMaxLength);
            material.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);
            material.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            material.Property(m => m.Versions).HasColumnName("density_versions").HasColumnType("jsonb")
                .HasConversion(VersionsJson.Converter, VersionsJson.Comparer);
            material.Ignore(m => m.DomainEvents);
            material.Ignore(m => m.AggregateKey);
        });
    }

    /// <summary>Versions as JSON with densities as strings: decimal values survive exactly (no double on the way).</summary>
    private static class VersionsJson
    {
        private sealed record Row(string EffectiveFrom, string? Bank, string? Loose, string? Compacted, string Moisture);

        public static readonly ValueConverter<IReadOnlyList<DensityVersion>, string> Converter = new(versions => Write(versions), json => Read(json));

        public static readonly ValueComparer<IReadOnlyList<DensityVersion>> Comparer = new(
            (a, b) => Write(a!) == Write(b!), versions => Write(versions).GetHashCode(StringComparison.Ordinal), versions => Read(Write(versions)));

        private static string Write(IReadOnlyList<DensityVersion> versions) =>
            JsonSerializer.Serialize(versions.Select(v => new Row(
                v.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Text(v.Bank), Text(v.Loose), Text(v.Compacted), Text(v.Moisture)!)));

        private static IReadOnlyList<DensityVersion> Read(string json) =>
            [.. (JsonSerializer.Deserialize<List<Row>>(json) ?? []).Select(r => new DensityVersion(
                DateOnly.ParseExact(r.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture), Number(r.Bank), Number(r.Loose), Number(r.Compacted), Number(r.Moisture)!.Value))];

        private static string? Text(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

        private static decimal? Number(string? text) => text is null ? null : decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class MaterialsDbContextFactory : IDesignTimeDbContextFactory<MaterialsDbContext>
{
    public MaterialsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MaterialsDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
