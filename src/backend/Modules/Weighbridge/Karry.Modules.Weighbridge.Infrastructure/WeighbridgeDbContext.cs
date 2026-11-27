using System.Data.Common;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Weighbridge.Application;
using Karry.Modules.Weighbridge.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Karry.Modules.Weighbridge.Infrastructure;

/// <summary>
/// Schema <c>weighbridge</c> (tenant-owned, RLS): haulers and trucks (P3.2, ADR-0042); tickets follow in P3.4. Masses are
/// whole kg in <c>numeric</c> columns named with their unit.
/// </summary>
public sealed class WeighbridgeDbContext(DbContextOptions<WeighbridgeDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "weighbridge";

    public override string Schema => SchemaName;

    public DbSet<Hauler> Haulers => Set<Hauler>();

    public DbSet<Truck> Trucks => Set<Truck>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Hauler>(hauler =>
        {
            hauler.ToTable("haulers");
            hauler.HasKey(h => h.Id);
            hauler.Property(h => h.Id).HasConversion(id => id.Value, value => new HaulerId(value)).ValueGeneratedNever();
            hauler.Property(h => h.Name).HasMaxLength(TruckRules.NameMaxLength);
            hauler.Property(h => h.NameKey).HasMaxLength(TruckRules.NameMaxLength);
            hauler.HasIndex(h => new { h.TenantId, h.NameKey }).IsUnique();
            hauler.Property(h => h.Phone).HasMaxLength(TruckRules.PhoneMaxLength);
            hauler.Property(h => h.Status).HasConversion<string>().HasMaxLength(20);
            hauler.Property(h => h.Version).IsRecordVersion();
            hauler.Ignore(h => h.DomainEvents);
            hauler.Ignore(h => h.AggregateKey);
        });

        modelBuilder.Entity<Truck>(truck =>
        {
            truck.ToTable("trucks", t =>
            {
                // A tare is complete or absent; masses are whole kg within what a road truck can be.
                t.HasCheckConstraint("ck_trucks_tare", "(tare_kg IS NULL) = (tare_on IS NULL) AND (tare_kg IS NULL) = (tare_method IS NULL)");
                t.HasCheckConstraint("ck_trucks_masses", "(tare_kg IS NULL OR tare_kg > 0) AND (max_payload_kg IS NULL OR max_payload_kg > 0)");
            });
            truck.HasKey(t => t.Id);
            truck.Property(t => t.Id).HasConversion(id => id.Value, value => new TruckId(value)).ValueGeneratedNever();
            truck.Property(t => t.Plate).HasMaxLength(TruckRules.PlateMaxLength);
            truck.Property(t => t.PlateKey).HasMaxLength(TruckRules.PlateMaxLength);

            // TC-P3-WB-016: one truck per plate in the company, however the plate was typed.
            truck.HasIndex(t => new { t.TenantId, t.PlateKey }).IsUnique();
            truck.Property(t => t.TareKg).HasPrecision(9, 0);
            truck.Property(t => t.MaxPayloadKg).HasPrecision(9, 0);
            truck.Property(t => t.TareMethod).HasConversion<string>().HasMaxLength(20);
            truck.Property(t => t.Axles).HasMaxLength(12);
            truck.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            truck.Property(t => t.Version).IsRecordVersion();
            truck.Ignore(t => t.Tare);
            truck.Ignore(t => t.Details);
            truck.Ignore(t => t.DomainEvents);
            truck.Ignore(t => t.AggregateKey);
        });
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no database connection is opened).</summary>
internal sealed class WeighbridgeDbContextFactory : IDesignTimeDbContextFactory<WeighbridgeDbContext>
{
    public WeighbridgeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WeighbridgeDbContext>().UseNpgsql("Host=design-time;Database=karry").UseSnakeCaseNamingConvention().Options);
}

internal sealed class HaulerStore(WeighbridgeDbContext db) : IHaulerStore
{
    public Task<Hauler?> FindAsync(HaulerId id, CancellationToken cancellationToken) => db.Haulers.SingleOrDefaultAsync(h => h.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Hauler>> ListAsync(bool includeArchived, CancellationToken cancellationToken) =>
        await db.Haulers.AsNoTracking().Where(h => includeArchived || h.Status == RecordStatus.Active).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> TrySaveAsync(Hauler hauler, CancellationToken cancellationToken)
    {
        if (db.Entry(hauler).State == EntityState.Detached)
        {
            db.Haulers.Add(hauler);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

internal sealed class TruckStore(WeighbridgeDbContext db) : ITruckStore
{
    public Task<Truck?> FindAsync(TruckId id, CancellationToken cancellationToken) => db.Trucks.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Truck>> SearchAsync(string? plateKey, TruckStatus? status, Guid? haulerId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Trucks.AsNoTracking();
        if (status is { } wanted)
        {
            query = query.Where(t => t.Status == wanted);
        }

        if (haulerId is { } hauler)
        {
            query = query.Where(t => t.HaulerId == hauler);
        }

        if (!string.IsNullOrEmpty(plateKey))
        {
            // The key holds letters and digits only: nothing to escape in the pattern.
            query = query.Where(t => t.PlateKey.Contains(plateKey))
                .OrderBy(t => t.PlateKey == plateKey ? 0 : t.PlateKey.StartsWith(plateKey) ? 1 : 2).ThenBy(t => t.PlateKey);
        }
        else
        {
            query = query.OrderBy(t => t.PlateKey);
        }

        return await query.Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TrySaveAsync(Truck truck, Func<DbConnection, DbTransaction, Task> alsoInTransaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alsoInTransaction);
        if (db.Entry(truck).State == EntityState.Detached)
        {
            db.Trucks.Add(truck);
        }

        // The truck, its outbox rows and ledger entries, and its review flag: one transaction.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await alsoInTransaction(db.Database.GetDbConnection(), transaction.GetDbTransaction()).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

public static class WeighbridgeInfrastructure
{
    /// <summary>Hauler and truck storage and operations; the outbox delivers the module's events.</summary>
    public static IServiceCollection AddWeighbridgeInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<WeighbridgeDbContext>(connectionString);
        services.AddOutboxDispatcher<WeighbridgeDbContext>(eventAssemblies: typeof(Truck).Assembly);
        services.AddScoped<IHaulerStore, HaulerStore>();
        services.AddScoped<HaulerOperations>();
        services.AddScoped<ITruckStore, TruckStore>();
        services.AddScoped<TruckOperations>();
        return services;
    }
}
