using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Assets.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Karry.Modules.Assets.Infrastructure;

/// <summary>
/// Schema <c>assets</c> (tenant-owned, RLS): the company's machines, their site history (append-only) and a trigram
/// search column (ADR-0026).
/// </summary>
public sealed class AssetsDbContext(DbContextOptions<AssetsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "assets";

    /// <summary>Lower-cased code, name, make, model, serial and plate, for <c>LIKE '%…%'</c> on a trigram index.</summary>
    public const string SearchText = "SearchText";

    public override string Schema => SchemaName;

    public DbSet<Asset> Assets => Set<Asset>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.Entity<Asset>(asset =>
        {
            asset.ToTable("assets", t => t.HasCheckConstraint("ck_assets_disposed", "(status = 'Disposed') = (disposed_at IS NOT NULL)"));
            asset.HasKey(a => a.Id);
            asset.Property(a => a.Id).HasConversion(id => id.Value, value => new AssetId(value)).ValueGeneratedNever();
            asset.Property(a => a.Code).HasMaxLength(AssetRules.CodeMaxLength);
            asset.HasIndex(a => new { a.TenantId, a.Code }).IsUnique();
            asset.Property(a => a.Name).HasMaxLength(AssetRules.NameMaxLength);
            asset.Property(a => a.Type).HasMaxLength(30);
            asset.Property(a => a.Make).HasMaxLength(AssetRules.MakeMaxLength);
            asset.Property(a => a.Model).HasMaxLength(AssetRules.MakeMaxLength);
            asset.Property(a => a.SerialNumber).HasMaxLength(AssetRules.SerialMaxLength);
            asset.Property(a => a.Plate).HasMaxLength(AssetRules.PlateMaxLength);
            asset.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            asset.HasIndex(a => new { a.TenantId, a.SiteId });
            asset.Property<string>(SearchText)
                .HasComputedColumnSql(
                    "lower(code || ' ' || name || ' ' || coalesce(make, '') || ' ' || coalesce(model, '') || ' ' || coalesce(serial_number, '') || ' ' || coalesce(plate, ''))",
                    stored: true);
            asset.HasIndex(SearchText).HasMethod("gin").HasOperators("gin_trgm_ops");
            asset.OwnsMany(a => a.Sites, site =>
            {
                site.ToTable("site_assignments");
                site.WithOwner().HasForeignKey("AssetId");
                site.Property<long>("Id").UseIdentityAlwaysColumn();
                site.HasKey("Id");
                site.Property(s => s.TenantId).HasConversion(id => id.Value, value => new TenantId(value));
                site.HasIndex(s => s.TenantId);
            });
            asset.Navigation(a => a.Sites).HasField("sites").UsePropertyAccessMode(PropertyAccessMode.Field);
            asset.Ignore(a => a.DomainEvents);
            asset.Ignore(a => a.AggregateKey);
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class AssetsDbContextFactory : IDesignTimeDbContextFactory<AssetsDbContext>
{
    public AssetsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AssetsDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
