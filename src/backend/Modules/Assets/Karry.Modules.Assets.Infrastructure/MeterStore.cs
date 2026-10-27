using Karry.Modules.Assets.Application;
using Karry.Modules.Assets.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Assets.Infrastructure;

internal sealed class MeterStore(AssetsDbContext db) : IMeterStore
{
    public async Task<IReadOnlyList<Meter>> ListAsync(AssetId asset, CancellationToken cancellationToken) =>
        await db.Meters.AsNoTracking().Where(m => m.AssetId == asset).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<Meter?> FindAsync(MeterId id, CancellationToken cancellationToken) =>
        db.Meters.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public void Add(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        db.Meters.Add(meter);
        db.MeterReadings.AddRange(meter.PendingReadings);
        meter.ClearPendingReadings();
    }

    public async Task<IReadOnlyList<Meter>> ForAssetsAsync(IReadOnlyCollection<AssetId> assets, CancellationToken cancellationToken) =>
        await db.Meters.Where(m => assets.Contains(m.AssetId)).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Stage(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        db.MeterReadings.AddRange(meter.PendingReadings);
        meter.ClearPendingReadings();
    }

    public async Task<bool> TrySaveAsync(Meter meter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meter);
        db.MeterReadings.AddRange(meter.PendingReadings);
        meter.ClearPendingReadings();
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<MeterReading>> ReadingsAsync(MeterId meter, Guid? after, int take, CancellationToken cancellationToken)
    {
        var readings = db.MeterReadings.AsNoTracking().Where(r => r.MeterId == meter);
        if (after is { } cursor && await db.MeterReadings.AsNoTracking().Where(r => r.Id == cursor && r.MeterId == meter).Select(r => (DateTimeOffset?)r.ReadAt).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } readAt)
        {
            readings = readings.Where(r => r.ReadAt < readAt || (r.ReadAt == readAt && r.Id.CompareTo(cursor) < 0));
        }

        return await readings.OrderByDescending(r => r.ReadAt).ThenByDescending(r => r.Id).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
