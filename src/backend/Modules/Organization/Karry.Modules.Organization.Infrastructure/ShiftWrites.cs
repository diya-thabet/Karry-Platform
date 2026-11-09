using Karry.Modules.Organization.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Organization.Infrastructure;

/// <summary>
/// Saves that change shift rules or instances run one at a time per company (hardening plan H-C1, finding C-6).
/// Concurrent changes of the same site's shifts insert and delete the same instance rows: without this they deadlocked
/// on the unique indexes, which the database reports as a transient error, so the API answered 503 in place of 409.
/// The lock is taken in the save's transaction and released with it; the later save then meets the stored rows and
/// fails with a unique violation or a concurrency error, which the stores report as a conflict.
/// </summary>
internal static class ShiftWrites
{
    public static async Task SaveChangesSerializedAsync(this OrganizationDbContext db, CancellationToken cancellationToken)
    {
        var tenants = Changed<ShiftInstance>(db, i => i.TenantId)
            .Concat(Changed<ShiftPattern>(db, p => p.TenantId))
            .Concat(Changed<NonWorkingDay>(db, d => d.TenantId))
            .Distinct().OrderBy(t => t.Value).ToList();
        if (tenants.Count == 0 || db.Database.CurrentTransaction is not null)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var tenant in tenants)
        {
            var key = $"organization.shifts:{tenant.Value}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<TenantId> Changed<T>(OrganizationDbContext db, Func<T, TenantId> tenant)
        where T : class =>
        db.ChangeTracker.Entries<T>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).Select(e => tenant(e.Entity));
}
