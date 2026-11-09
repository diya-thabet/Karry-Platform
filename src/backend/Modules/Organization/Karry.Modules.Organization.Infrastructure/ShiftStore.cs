using Karry.Modules.Organization.Application;
using Karry.Modules.Organization.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.Organization.Infrastructure;

/// <summary>
/// Reads include the unit of work's pending changes (added rows, not removed ones): a change and the instances it
/// produces are computed together and saved in one transaction.
/// </summary>
internal sealed class ShiftStore(OrganizationDbContext db) : IShiftStore
{
    public async Task<IReadOnlyList<ShiftPattern>> PatternsAsync(SiteId site, CancellationToken cancellationToken) =>
        WithPending(await db.ShiftPatterns.Where(p => p.SiteId == site).ToListAsync(cancellationToken).ConfigureAwait(false), p => p.SiteId == site);

    public async Task<IReadOnlyList<NonWorkingDay>> NonWorkingDaysAsync(SiteId site, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        WithPending(
            await db.NonWorkingDays.Where(d => d.SiteId == site && d.Date >= from && d.Date <= to).ToListAsync(cancellationToken).ConfigureAwait(false),
            d => d.SiteId == site && d.Date >= from && d.Date <= to);

    public async Task<IReadOnlyList<ShiftInstance>> InstancesAsync(SiteId site, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        WithPending(
            await db.ShiftInstances.Where(i => i.SiteId == site && i.Date >= from && i.Date <= to).ToListAsync(cancellationToken).ConfigureAwait(false),
            i => i.SiteId == site && i.Date >= from && i.Date <= to);

    public void Add(ShiftPattern pattern) => db.ShiftPatterns.Add(pattern);

    public void Remove(ShiftPattern pattern) => db.ShiftPatterns.Remove(pattern);

    public void Add(NonWorkingDay day) => db.NonWorkingDays.Add(day);

    public void Remove(NonWorkingDay day) => db.NonWorkingDays.Remove(day);

    public void Add(ShiftInstance instance) => db.ShiftInstances.Add(instance);

    public void Remove(ShiftInstance instance) => db.ShiftInstances.Remove(instance);

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesSerializedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private List<T> WithPending<T>(List<T> stored, Func<T, bool> matches)
        where T : class
    {
        var added = db.ChangeTracker.Entries<T>().Where(e => e.State == EntityState.Added && matches(e.Entity)).Select(e => e.Entity);
        return [.. stored.Where(e => db.Entry(e).State != EntityState.Deleted).Concat(added).Distinct()];
    }
}
