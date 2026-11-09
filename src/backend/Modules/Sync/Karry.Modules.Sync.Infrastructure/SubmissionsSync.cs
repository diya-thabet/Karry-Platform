using Karry.BuildingBlocks.Sync;
using Karry.Modules.Sync.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Sync.Infrastructure;

/// <summary>
/// The status of a command the tablet sent (US-P2-OFF-04 AC1, ADR-0031 §6), keyed by the command's ID: <c>accepted</c>,
/// <c>conflict</c> (with <see cref="Flags"/>), <c>waiting</c> (for an earlier number) or <c>rejected</c> (<see cref="Code"/>,
/// a type the server did not know). <see cref="At"/> is when it took this status. Later statuses of the record itself
/// ("approved", "rejected by the controller") join from P2.8.
/// </summary>
public sealed record SubmissionSyncRow(Guid Id, long Sequence, string Status, string? Code, IReadOnlyList<string> Flags, Guid? RecordId, DateTimeOffset At);

/// <summary>
/// The <c>submissions</c> collection of the tablet's pull: what became of the commands this tablet sent, so that one
/// answered <c>waiting</c>, or sent while the answer was lost, gets its final status (ADR-0031 §6). A snapshot holds
/// the last <see cref="History"/>; a delta, what changed since the cursor. Only this tablet's commands.
/// </summary>
internal sealed class SubmissionsSync(SyncDbContext db, TimeProvider clock) : ISyncSource
{
    public const string Collection = "submissions";

    /// <summary>What a snapshot covers: the field app shows 14 days of history.</summary>
    public static readonly TimeSpan History = TimeSpan.FromDays(14);

    public async Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var start = since ?? clock.GetUtcNow() - History;
        var device = scope.DeviceId;

        var processed = await (
                from p in db.Processed.AsNoTracking()
                join i in db.Inbox.AsNoTracking() on new { p.DeviceId, Id = p.CommandId } equals new { i.DeviceId, i.Id }
                where p.DeviceId == device && p.HandledAt >= start
                select new { i.Id, i.Sequence, p.Outcome, p.Code, p.Flags, p.RecordId, p.HandledAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var waiting = await db.Waiting.AsNoTracking()
            .Where(w => w.DeviceId == device && w.ReceivedAt >= start && !db.Inbox.Any(i => i.DeviceId == w.DeviceId && i.Id == w.Id))
            .Select(w => new { w.Id, w.Sequence, w.ReceivedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var unhandled = await db.Unhandled.AsNoTracking()
            .Where(u => u.DeviceId == device && u.ReceivedAt >= start && !db.Inbox.Any(i => i.DeviceId == u.DeviceId && i.Id == u.Id))
            .Select(u => new { u.Id, u.Sequence, u.Reason, u.ReceivedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // One row per command: a handled command's outcome wins over its earlier "waiting" or "type unknown".
        var rows = new Dictionary<CommandId, SubmissionSyncRow>();
        foreach (var u in unhandled)
        {
            rows[u.Id] = new SubmissionSyncRow(u.Id.Value, u.Sequence, "rejected", u.Reason, [], null, u.ReceivedAt);
        }

        foreach (var w in waiting)
        {
            rows[w.Id] = new SubmissionSyncRow(w.Id.Value, w.Sequence, "waiting", null, [], null, w.ReceivedAt);
        }

        foreach (var p in processed)
        {
            rows[p.Id] = new SubmissionSyncRow(p.Id.Value, p.Sequence, p.Outcome.ToString().ToLowerInvariant(), p.Code, p.Flags, p.RecordId, p.HandledAt);
        }

        return [new SyncDelta(Collection, [.. rows.Values.OrderBy(r => r.Sequence)], [])];
    }
}
