using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Organization.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Karry.Modules.Organization.Infrastructure;

/// <summary>Schema <c>organization</c>: sites (tenant-owned, RLS), with PostGIS geometry through NetTopologySuite (ADR-0024).</summary>
public sealed class OrganizationDbContext(DbContextOptions<OrganizationDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "organization";

    public override string Schema => SchemaName;

    public DbSet<Site> Sites => Set<Site>();

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
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class OrganizationDbContextFactory : IDesignTimeDbContextFactory<OrganizationDbContext>
{
    public OrganizationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OrganizationDbContext>()
            .UseNpgsql("Host=design-time;Database=karry", npgsql => npgsql.UseNetTopologySuite()).UseSnakeCaseNamingConvention().Options);
}
