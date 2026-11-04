using System.Text.Json;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Organization.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;

namespace Karry.Modules.Organization.Infrastructure;

/// <summary>
/// Schema <c>organization</c> (tenant-owned, RLS): sites with PostGIS geometry through NetTopologySuite, shift pattern
/// versions, non-working days and the stored shift instances (ADR-0024).
/// </summary>
public sealed class OrganizationDbContext(DbContextOptions<OrganizationDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "organization";

    public override string Schema => SchemaName;

    public DbSet<Site> Sites => Set<Site>();

    public DbSet<ShiftPattern> ShiftPatterns => Set<ShiftPattern>();

    public DbSet<NonWorkingDay> NonWorkingDays => Set<NonWorkingDay>();

    public DbSet<ShiftInstance> ShiftInstances => Set<ShiftInstance>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseNpgsql(npgsql => npgsql.UseNetTopologySuite());
    }

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.Entity<Site>(site =>
        {
            site.ToTable("sites", t =>
            {
                t.HasCheckConstraint("ck_sites_boundary_valid", "boundary IS NULL OR ST_IsValid(boundary)");
                t.HasCheckConstraint("ck_sites_archived", "(status = 'Archived') = (archived_at IS NOT NULL)");
            });
            site.Property(s => s.Version).IsRecordVersion();
            site.HasKey(s => s.Id);
            site.Property(s => s.Id).HasConversion(id => id.Value, value => new SiteId(value)).ValueGeneratedNever();
            site.Property(s => s.Code).HasMaxLength(SiteRules.CodeMaxLength);
            site.HasIndex(s => new { s.TenantId, s.Code }).IsUnique();
            site.Property(s => s.Name).HasMaxLength(SiteRules.NameMaxLength);
            site.Property(s => s.Type).HasConversion<string>().HasMaxLength(20);
            site.Property(s => s.AddressLine).HasMaxLength(SiteRules.AddressMaxLength);
            site.Property(s => s.City).HasMaxLength(SiteRules.NameMaxLength);
            site.Property(s => s.Region).HasMaxLength(SiteRules.NameMaxLength);
            site.Property(s => s.PostalCode).HasMaxLength(20);
            site.Property(s => s.Country).HasMaxLength(2);
            site.Property(s => s.Location).HasColumnType($"geometry(Point, {SiteRules.Srid})");
            site.Property(s => s.Boundary).HasColumnType($"geometry(Polygon, {SiteRules.Srid})");
            site.Property(s => s.TimeZone).HasMaxLength(64);
            site.Property(s => s.Currency).HasMaxLength(3);
            site.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            site.Ignore(s => s.Address);
            site.Ignore(s => s.Details);
            site.Ignore(s => s.DomainEvents);
            site.Ignore(s => s.AggregateKey);
        });

        var siteId = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<SiteId, Guid>(id => id.Value, value => new SiteId(value));

        modelBuilder.Entity<ShiftPattern>(pattern =>
        {
            pattern.ToTable("shift_patterns");
            pattern.HasKey(p => p.Id);
            pattern.Property(p => p.Id).HasConversion(id => id.Value, value => new ShiftPatternId(value)).ValueGeneratedNever();
            pattern.Property(p => p.SiteId).HasConversion(siteId);
            pattern.HasOne<Site>().WithMany().HasForeignKey(p => p.SiteId).OnDelete(DeleteBehavior.Restrict);
            pattern.HasIndex(p => new { p.TenantId, p.SiteId, p.EffectiveFrom }).IsUnique();
            pattern.Property(p => p.Shifts).HasColumnType("jsonb").HasConversion(ShiftsJson.Converter, ShiftsJson.Comparer);
            pattern.Ignore(p => p.DomainEvents);
            pattern.Ignore(p => p.AggregateKey);
        });

        modelBuilder.Entity<NonWorkingDay>(day =>
        {
            day.ToTable("non_working_days");
            day.HasKey(d => d.Id);
            day.Property(d => d.Id).HasConversion(id => id.Value, value => new NonWorkingDayId(value)).ValueGeneratedNever();
            day.Property(d => d.SiteId).HasConversion(siteId);
            day.HasOne<Site>().WithMany().HasForeignKey(d => d.SiteId).OnDelete(DeleteBehavior.Restrict);
            day.HasIndex(d => new { d.TenantId, d.SiteId, d.Date }).IsUnique();
            day.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
            day.Property(d => d.Name).HasMaxLength(ShiftRules.DayNameMaxLength);
            day.Ignore(d => d.DomainEvents);
            day.Ignore(d => d.AggregateKey);
        });

        modelBuilder.Entity<ShiftInstance>(instance =>
        {
            instance.ToTable("shift_instances", t => t.HasCheckConstraint("ck_shift_instances_order", "ends_at > starts_at"));
            instance.HasKey(i => i.Id);
            instance.Property(i => i.Id).ValueGeneratedNever();
            instance.Property(i => i.SiteId).HasConversion(siteId);
            instance.HasOne<Site>().WithMany().HasForeignKey(i => i.SiteId).OnDelete(DeleteBehavior.Restrict);
            instance.HasIndex(i => new { i.TenantId, i.SiteId, i.Date, i.ShiftCode }).IsUnique();
            instance.HasIndex(i => new { i.SiteId, i.StartsAt });
            instance.Property(i => i.ShiftCode).HasMaxLength(ShiftRules.CodeMaxLength);
            instance.Property(i => i.ShiftName).HasMaxLength(ShiftRules.NameMaxLength);
            instance.Property(i => i.TimeZone).HasMaxLength(64);
        });
    }

    /// <summary>A version's shifts as one JSON document (always read and written whole).</summary>
    private static class ShiftsJson
    {
        private sealed record Row(string Code, string Name, string Start, string End, IReadOnlyList<string> Days);

        public static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<IReadOnlyList<ShiftDefinition>, string> Converter =
            new(shifts => Write(shifts), json => Read(json));

        public static readonly ValueComparer<IReadOnlyList<ShiftDefinition>> Comparer = new(
            (a, b) => Write(a!) == Write(b!), shifts => Write(shifts).GetHashCode(StringComparison.Ordinal), shifts => Read(Write(shifts)));

        private static string Write(IReadOnlyList<ShiftDefinition> shifts) =>
            JsonSerializer.Serialize(ShiftRules.Snapshot(shifts).Select(s => new Row(s.Code, s.Name, s.Start, s.End, s.Days)));

        private static IReadOnlyList<ShiftDefinition> Read(string json)
        {
            var rows = JsonSerializer.Deserialize<List<Row>>(json) ?? [];
            var shifts = ShiftRules.Validate([.. rows.Select(r => ((string?)r.Code, (string?)r.Name, (string?)r.Start, (string?)r.End, (IReadOnlyList<string>?)r.Days))]);
            return shifts.IsSuccess ? shifts.Value : throw new InvalidOperationException($"Stored shifts are invalid: {shifts.Error.Code}.");
        }
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class OrganizationDbContextFactory : IDesignTimeDbContextFactory<OrganizationDbContext>
{
    public OrganizationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OrganizationDbContext>()
            .UseNpgsql("Host=design-time;Database=karry", npgsql => npgsql.UseNetTopologySuite()).UseSnakeCaseNamingConvention().Options);
}
