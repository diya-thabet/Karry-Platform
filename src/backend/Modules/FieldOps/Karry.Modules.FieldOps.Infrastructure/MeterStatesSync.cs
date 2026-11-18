using System.Globalization;
using Karry.BuildingBlocks.Sync;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Karry.Modules.FieldOps.Infrastructure;

/// <summary>
/// Where a meter of the tablet's site stands (P2.5, US-P2-FO-01 AC2, US-P2-FO-04 AC1): <see cref="LastEnd"/>, the end
/// of its last ended segment (the reference a start is checked against), and <see cref="Open"/>, its last segment when
/// that one is still running (to end it, or to hand the machine over). Meter values are written as the records write
/// them (<c>"12482.5"</c>).
/// </summary>
public sealed record MeterStateSyncRow(Guid Id, MeterEndRow? LastEnd, OpenSegmentRow? Open);

public sealed record MeterEndRow(Guid EntryId, string Value, DateTimeOffset At);

public sealed record OpenSegmentRow(Guid EntryId, int Version, Guid OperatorId, DateTimeOffset StartedAt, string StartValue, DateOnly? ShiftDate, string? ShiftCode);

/// <summary>
/// The <c>meterStates</c> collection of the tablet's pull (P2.5): FieldOps owns the answer, since the Assets meters carry
/// no value written by shift entries. Rules as the start handler applies them (ADR-0034 §5): the meter's last segment
/// is its latest started current version; it is open, or it gives the last end. A snapshot holds every meter with an
/// entry on the site; a delta, the meters with a version received since the cursor (a meter whose entries are all on
/// another site is a remove).
/// </summary>
internal sealed class MeterStatesSync(FieldOpsDbContext db) : ISyncSource
{
    public const string Collection = "meterStates";

    // The entry of the latest started current version of each meter of the site, among the given statuses. Only the ID
    // comes back: EF cannot map raw SQL onto an entity with optional complex properties (the positions).
    private const string LatestPerMeter = """
        SELECT DISTINCT ON (e.meter_id) e.root_id AS "Value"
        FROM fieldops.shift_entries e
        WHERE e.site_id = @site AND (@all OR e.meter_id = ANY(@meters)) AND e.status = ANY(@statuses)
          AND NOT EXISTS (SELECT 1 FROM fieldops.shift_entries l WHERE l.tenant_id = e.tenant_id AND l.root_id = e.root_id AND l.version > e.version)
        ORDER BY e.meter_id, e.started_at DESC, e.root_id DESC
        """;

    public async Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Guid[] changed = [];
        if (since is { } from)
        {
            changed = [.. await db.ShiftEntries.AsNoTracking().Where(e => e.ReceivedAt >= from).Select(e => e.MeterId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)];
            if (changed.Length == 0)
            {
                return [new SyncDelta(Collection, [], [])];
            }
        }

        var latest = await LatestAsync(scope.SiteId, since is null, changed, [nameof(ShiftEntryStatus.Open), nameof(ShiftEntryStatus.Closed)], cancellationToken).ConfigureAwait(false);
        var ended = (await LatestAsync(scope.SiteId, since is null, changed, [nameof(ShiftEntryStatus.Closed)], cancellationToken).ConfigureAwait(false))
            .ToDictionary(e => e.MeterId);

        var rows = latest
            .Select(e => new MeterStateSyncRow(
                e.MeterId,
                ended.TryGetValue(e.MeterId, out var end) ? new MeterEndRow(end.RootId, Text(end.EndValue!.Value), end.EndedAt!.Value) : null,
                e.Status == ShiftEntryStatus.Open ? new OpenSegmentRow(e.RootId, e.Version, e.OperatorId, e.StartedAt, Text(e.StartValue), e.ShiftDate, e.ShiftCode) : null))
            .OrderBy(r => r.Id)
            .ToList();
        var kept = rows.Select(r => r.Id).ToHashSet();
        return [new SyncDelta(Collection, [.. rows], [.. changed.Where(id => !kept.Contains(id))])];
    }

    private async Task<List<ShiftEntryVersion>> LatestAsync(Guid site, bool all, Guid[] meters, string[] statuses, CancellationToken cancellationToken)
    {
        var roots = await db.Database.SqlQueryRaw<Guid>(
                LatestPerMeter,
                new NpgsqlParameter("site", NpgsqlDbType.Uuid) { Value = site },
                new NpgsqlParameter("all", NpgsqlDbType.Boolean) { Value = all },
                new NpgsqlParameter("meters", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = meters },
                new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = statuses })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await db.ShiftEntries.AsNoTracking()
            .Where(e => roots.Contains(e.RootId) && !db.ShiftEntries.Any(later => later.RootId == e.RootId && later.Version > e.Version))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A meter value with its one decimal, as the records write it.</summary>
    private static string Text(decimal value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
