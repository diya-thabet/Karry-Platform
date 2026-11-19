using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Assets.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Karry.Modules.Assets.Infrastructure;

/// <summary>
/// Schema <c>assets</c> (tenant-owned, RLS): the company's machines, their site history (append-only), a trigram search
/// column, and their meters with the readings (append-only) (ADR-0026).
/// </summary>
public sealed class AssetsDbContext(DbContextOptions<AssetsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "assets";

    /// <summary>Lower-cased code, name, make, model, serial and plate, for <c>LIKE '%…%'</c> on a trigram index.</summary>
    public const string SearchText = "SearchText";

    public override string Schema => SchemaName;

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<Meter> Meters => Set<Meter>();

    public DbSet<MeterReading> MeterReadings => Set<MeterReading>();

    public DbSet<MeterEvent> MeterEvents => Set<MeterEvent>();

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
            asset.Property(a => a.Version).IsRecordVersion();
            asset.HasIndex(a => new { a.TenantId, a.SiteId });
            asset.Property(a => a.LabelToken).HasMaxLength(AssetLabels.TokenLength).IsFixedLength();
            asset.HasIndex(a => a.LabelToken).IsUnique();
            asset.OwnsOne(a => a.Photo, photo =>
            {
                photo.Property(p => p.Sha256).HasColumnName("photo_sha256").HasMaxLength(64).IsFixedLength();
                photo.Property(p => p.ContentType).HasColumnName("photo_content_type").HasMaxLength(20);
                photo.Property(p => p.Bytes).HasColumnName("photo_bytes");
            });
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

        modelBuilder.Entity<Meter>(meter =>
        {
            meter.ToTable("meters", t => t.HasCheckConstraint("ck_meters_values", "initial_reading >= 0 AND last_value >= 0"));
            meter.HasKey(m => m.Id);
            meter.Property(m => m.Id).HasConversion(id => id.Value, value => new MeterId(value)).ValueGeneratedNever();
            meter.Property(m => m.AssetId).HasConversion(id => id.Value, value => new AssetId(value));
            meter.HasOne<Asset>().WithMany().HasForeignKey(m => m.AssetId).OnDelete(DeleteBehavior.Restrict);
            meter.Property(m => m.Kind).HasConversion<string>().HasMaxLength(10);
            meter.Property(m => m.Status).HasConversion<string>().HasMaxLength(10);
            meter.Property(m => m.InitialReading).HasPrecision(9, MeterRules.Decimals);
            meter.Property(m => m.Offset).HasPrecision(9, MeterRules.Decimals);
            meter.Property(m => m.LastValue).HasPrecision(9, MeterRules.Decimals);

            // Two readings at once on one meter: the second save fails instead of breaking "never goes back".
            meter.Property<uint>("xmin").IsRowVersion();
            meter.Ignore(m => m.PendingReadings);
            meter.Ignore(m => m.PendingEvents);
            meter.Ignore(m => m.DomainEvents);
            meter.Ignore(m => m.AggregateKey);
        });

        modelBuilder.Entity<MeterReading>(reading =>
        {
            reading.ToTable("meter_readings");
            reading.HasKey(r => r.Id);
            reading.Property(r => r.Id).ValueGeneratedNever();
            reading.Property(r => r.MeterId).HasConversion(id => id.Value, value => new MeterId(value));
            reading.HasOne<Meter>().WithMany().HasForeignKey(r => r.MeterId).OnDelete(DeleteBehavior.Restrict);
            reading.Property(r => r.Value).HasPrecision(9, MeterRules.Decimals);
            reading.Property(r => r.RecordedBy).HasMaxLength(200);
            reading.HasIndex(r => new { r.MeterId, r.ReadAt, r.Id });
        });

        modelBuilder.Entity<MeterEvent>(meterEvent =>
        {
            meterEvent.ToTable("meter_events", t => t.HasCheckConstraint(
                "ck_meter_events_values",
                "(kind IN ('Replaced', 'Reset') AND value_before IS NOT NULL AND value_after IS NOT NULL) "
                + "OR (kind = 'Failed' AND value_after IS NULL) OR (kind = 'Repaired' AND value_before IS NULL AND value_after IS NOT NULL)"));
            meterEvent.HasKey(e => e.Id);
            meterEvent.Property(e => e.Id).ValueGeneratedNever();
            meterEvent.Property(e => e.MeterId).HasConversion(id => id.Value, value => new MeterId(value));
            meterEvent.Property(e => e.AssetId).HasConversion(id => id.Value, value => new AssetId(value));
            meterEvent.HasOne<Meter>().WithMany().HasForeignKey(e => e.MeterId).OnDelete(DeleteBehavior.Restrict);
            meterEvent.Property(e => e.Kind).HasConversion<string>().HasMaxLength(10);
            meterEvent.Property(e => e.ValueBefore).HasPrecision(9, MeterRules.Decimals);
            meterEvent.Property(e => e.ValueAfter).HasPrecision(9, MeterRules.Decimals);
            meterEvent.Property(e => e.Reason).HasMaxLength(MeterRules.MaxReason);
            meterEvent.Property(e => e.Photos).HasColumnType("text[]").HasConversion(
                photos => photos.ToArray(),
                stored => stored,
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IReadOnlyList<string>>(
                    (a, b) => a!.SequenceEqual(b!), list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode(StringComparison.Ordinal))), list => list.ToArray()));
            meterEvent.Property(e => e.RecordedBy).HasMaxLength(200);
            meterEvent.HasIndex(e => new { e.TenantId, e.MeterId, e.OccurredAt });
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class AssetsDbContextFactory : IDesignTimeDbContextFactory<AssetsDbContext>
{
    public AssetsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AssetsDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}
