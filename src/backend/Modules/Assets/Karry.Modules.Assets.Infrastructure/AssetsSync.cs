using Karry.BuildingBlocks.Sync;
using Karry.Modules.Assets.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Assets.Infrastructure;

/// <summary>A machine as the field app keeps it (<c>@karry/offline</c> <c>AssetRow</c>): <see cref="LabelCode"/> is what its QR label encodes.</summary>
public sealed record AssetSyncRow(
    Guid Id, string Code, string Name, string Type, Guid SiteId, string Status, string? Make, string? Model, string? SerialNumber, string? Plate, string LabelCode);

public sealed record MeterSyncRow(Guid Id, Guid AssetId, string Kind, string Unit, string Status);

/// <summary>
/// The machines of the tablet's site and their meters (ADR-0028 §5). A machine moved to another site or disposed, and
/// the meters of such a machine or a replaced meter, are sent as removes; a meter changes with its machine.
/// </summary>
internal sealed class AssetsSync(AssetsDbContext db) : ISyncSource
{
    public async Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var site = scope.SiteId;
        var assets = await db.Assets.AsNoTracking()
            .Where(a => since == null ? a.SiteId == site && a.Status != AssetStatus.Disposed : a.ChangedAt >= since)
            .OrderBy(a => a.Code)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var meters = await (
                from meter in db.Meters.AsNoTracking()
                join asset in db.Assets.AsNoTracking() on meter.AssetId equals asset.Id
                where since == null
                    ? asset.SiteId == site && asset.Status != AssetStatus.Disposed && meter.Status != MeterStatus.Replaced
                    : meter.ChangedAt >= since || asset.ChangedAt >= since
                select new { Meter = meter, InScope = asset.SiteId == site && asset.Status != AssetStatus.Disposed && meter.Status != MeterStatus.Replaced })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var inScope = assets.Where(a => a.SiteId == site && a.Status != AssetStatus.Disposed).ToList();
        return
        [
            new SyncDelta("assets", [.. inScope.Select(Row)], [.. assets.Except(inScope).Select(a => a.Id.Value)]),
            new SyncDelta(
                "meters",
                [.. meters.Where(m => m.InScope).Select(m => Row(m.Meter))],
                [.. meters.Where(m => !m.InScope).Select(m => m.Meter.Id.Value)]),
        ];
    }

    private static AssetSyncRow Row(Asset a) => new(
        a.Id.Value, a.Code, a.Name, a.Type, a.SiteId, AssetRules.StatusCode(a.Status), a.Make, a.Model, a.SerialNumber, a.Plate, AssetLabels.Text(a.LabelToken));

    private static MeterSyncRow Row(Meter m) => new(m.Id.Value, m.AssetId.Value, AssetRules.MeterKindCode(m.Kind), MeterRules.UnitCode(m.Kind), MeterRules.StatusCode(m.Status));
}
