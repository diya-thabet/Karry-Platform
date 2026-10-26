using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Tenancy.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Tenancy.Infrastructure;

/// <summary>Schema <c>tenancy</c>: the tenant catalog (platform data: no tenant_id, no RLS; see global-reference-tables).</summary>
public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "tenancy";

    public override string Schema => SchemaName;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>Company logos: public by design (sign-in page, threat model R-07), so platform data like the catalog.</summary>
    public DbSet<TenantLogo> Logos => Set<TenantLogo>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants");
            tenant.HasKey(t => t.Id);
            tenant.Property(t => t.Id).HasConversion(id => id.Value, value => new TenantId(value)).ValueGeneratedNever();
            tenant.Property(t => t.Slug).HasConversion(slug => slug.Value, value => TenantSlug.FromStorage(value)).HasMaxLength(TenantSlug.MaxLength);
            tenant.HasIndex(t => t.Slug).IsUnique();
            tenant.Property(t => t.Name).HasMaxLength(Tenant.NameMaxLength);
            tenant.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            tenant.Property(t => t.LegalName).HasMaxLength(TenantSettings.LegalNameMaxLength);
            tenant.Property(t => t.PrimaryColour).HasMaxLength(7);
            tenant.Property(t => t.Language).HasMaxLength(8);
            tenant.Property(t => t.Currency).HasMaxLength(3);
            tenant.Property(t => t.UnitSystem).HasConversion<string>().HasMaxLength(20);
            tenant.Property(t => t.TimeZone).HasMaxLength(64);
            tenant.Property(t => t.LogoSha256).HasMaxLength(64);
            tenant.ToTable(t => t.HasCheckConstraint("ck_tenants_fiscal_year_start_month", "fiscal_year_start_month BETWEEN 1 AND 12"));
            tenant.Ignore(t => t.Settings);
            tenant.Ignore(t => t.DomainEvents);
            tenant.Ignore(t => t.AggregateKey);
        });

        modelBuilder.Entity<TenantLogo>(logo =>
        {
            logo.ToTable("tenant_logos", t => t.HasCheckConstraint("ck_tenant_logos_size", $"octet_length(content) <= {TenantLogo.MaxBytes}"));
            logo.HasKey(l => l.TenantId);
            logo.Property(l => l.ContentType).HasMaxLength(20);
            logo.Property(l => l.Sha256).HasMaxLength(64);
        });
    }
}
