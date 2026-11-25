using Karry.BuildingBlocks.Sync;
using Karry.Modules.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Notifications.Infrastructure;

/// <summary>A notice as the tablet keeps it (ADR-0039 §3): for whom, what, the values its words name, when.</summary>
public sealed record NoticeSyncRow(Guid Id, Guid RecipientId, string Type, IReadOnlyDictionary<string, string> Parameters, DateTimeOffset CreatedAt);

/// <summary>
/// The <c>notices</c> collection of the tablet's pull (ADR-0039 §3, TC-P2-NOT-001): the notices of the tablet's site
/// that concern people who work on tablets (a rejected entry), over the operator history's 14 days, whole at every
/// pull (<c>replace</c>). The tablet shows each person only theirs, once they unlock it; whether one was read stays
/// on the tablet. The site's decided entries already travel to the same tablets (<c>reviewOutcomes</c>), so this
/// carries nothing more about another operator than what the tablet holds.
/// </summary>
internal sealed class NoticesSync(NotificationsDbContext db, TimeProvider clock) : ISyncSource
{
    public const string Name = "notices";

    /// <summary>The notice types a tablet shows.</summary>
    public static readonly IReadOnlyList<string> TabletTypes = [NoticeTypes.ReviewRejected];

    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public async Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var from = clock.GetUtcNow() - Window;
        var types = TabletTypes.ToList();
        var notices = await db.Notifications.AsNoTracking()
            .Where(n => n.SiteId == scope.SiteId && types.Contains(n.Type) && n.CreatedAt >= from)
            .OrderBy(n => n.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [new SyncDelta(Name, [.. notices.Select(n => new NoticeSyncRow(n.Id.Value, n.RecipientId, n.Type, n.Parameters, n.CreatedAt))], [], Replace: true)];
    }
}
