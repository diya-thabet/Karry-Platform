using Karry.Modules.Assets.Contracts;
using Karry.Modules.Assets.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Karry.Modules.Assets.Infrastructure;

/// <summary>Meters for other modules (ADR-0034): the meter, its machine and the machine's site history.</summary>
internal sealed class MeterDirectory(AssetsDbContext db) : IMeterDirectory
{
    public async Task<MeterSummary?> FindAsync(Guid meterId, CancellationToken cancellationToken)
    {
        var found = await db.Meters.AsNoTracking()
            .Where(m => m.Id == new MeterId(meterId))
            .Join(db.Assets.AsNoTracking(), m => m.AssetId, a => a.Id, (m, a) => new { Meter = m, Asset = a })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        return new MeterSummary(
            meterId, found.Asset.Id.Value, found.Asset.Code, AssetRules.MeterKindCode(found.Meter.Kind), MeterRules.StatusCode(found.Meter.Status),
            [.. found.Asset.Sites.OrderBy(s => s.From).Select(s => new AssetSiteAssignment(s.SiteId, s.From))]);
    }
}

/// <summary>Meter events for other modules (ADR-0036 §5), oldest first.</summary>
internal sealed class MeterEvents(AssetsDbContext db) : IMeterEvents
{
    public async Task<IReadOnlyList<MeterEventSummary>> OfMeterAsync(Guid meterId, CancellationToken cancellationToken) =>
        [.. (await db.MeterEvents.AsNoTracking().Where(e => e.MeterId == new MeterId(meterId)).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Summary)];

    public static MeterEventSummary Summary(MeterEvent e) => new(e.Id, e.MeterId.Value, MeterRules.EventKindCode(e.Kind), e.OccurredAt, e.ValueBefore, e.ValueAfter);
}

/// <summary>
/// Publishes <see cref="MeterEventRecorded"/> to other modules through <see cref="IMeterEventSubscriber"/>, in the
/// dispatcher's transaction (they may not reference this module's domain events). A failing handler fails the message,
/// which the dispatcher retries for every handler: they are idempotent by contract.
/// </summary>
internal sealed class MeterEventFanOut(IEnumerable<IMeterEventSubscriber> handlers) : BuildingBlocks.Outbox.IDomainEventSubscriber<MeterEventRecorded>
{
    public async Task HandleAsync(MeterEventRecorded domainEvent, BuildingBlocks.Outbox.DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(context);
        var summary = new MeterEventSummary(domainEvent.MeterEventId, domainEvent.MeterId, domainEvent.Kind, domainEvent.HappenedAt, domainEvent.ValueBefore, domainEvent.ValueAfter);
        var connection = context.Db.Database.GetDbConnection();
        var transaction = context.Db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Meter events are handed on in the dispatcher's transaction.");
        foreach (var handler in handlers)
        {
            await handler.HandleAsync(summary, connection, transaction, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Machines for other modules (ADR-0035): the machine, its type and its site history.</summary>
internal sealed class AssetDirectory(AssetsDbContext db) : IAssetDirectory
{
    public async Task<AssetSummary?> FindAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == new AssetId(assetId), cancellationToken).ConfigureAwait(false);
        return asset is null
            ? null
            : new AssetSummary(assetId, asset.Code, asset.Type, [.. asset.Sites.OrderBy(s => s.From).Select(s => new AssetSiteAssignment(s.SiteId, s.From))]);
    }
}
