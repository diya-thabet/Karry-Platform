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
            tenant.Ignore(t => t.DomainEvents);
            tenant.Ignore(t => t.AggregateKey);
        });
    }
}
