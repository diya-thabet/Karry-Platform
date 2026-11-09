using Karry.Modules.Sync.Application;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Sync.Infrastructure;

internal sealed class WaitingReport(SyncDbContext db) : IWaitingReport
{
    public async Task<IReadOnlyList<DeviceGap>> DevicesAsync(CancellationToken cancellationToken) =>
        // Still waiting: no inbox row for the command yet (a handled waiting command keeps its row, append-only).
        await db.Waiting.Where(w => !db.Inbox.Any(i => i.DeviceId == w.DeviceId && i.Id == w.Id))
            .GroupBy(w => w.DeviceId)
            .Select(g => new DeviceGap(g.Key, g.Count(), g.Min(w => w.ReceivedAt)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
