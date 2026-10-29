using Karry.Modules.Trust.Application;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

internal sealed class EfAuditLog(TrustDbContext db) : IAuditLog
{
    public async Task<IReadOnlyList<AuditEntry>> SearchAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Left join: an entry of an event type this version does not know has no subject; it shows only unfiltered
        // and to company-wide readers.
        var rows =
            from entry in db.LedgerEntries.AsNoTracking()
            join indexed in db.LedgerSubjects.AsNoTracking() on entry.Seq equals indexed.Seq into subjects
            from subject in subjects.DefaultIfEmpty()
            select new { entry, subject };

        if (query.Before is { } before)
        {
            rows = rows.Where(r => r.entry.Seq < before);
        }

        if (query.Sites is { } sites)
        {
            rows = rows.Where(r => r.subject != null && r.subject.SiteId != null && sites.Contains(r.subject.SiteId.Value));
        }

        if (query.SiteId is { } site)
        {
            rows = rows.Where(r => r.subject != null && r.subject.SiteId == site);
        }

        if (query.Actor is { } actor)
        {
            rows = rows.Where(r => r.subject != null && r.subject.Actor == actor);
        }

        if (query.EntityType is { } entityType)
        {
            rows = rows.Where(r => r.subject != null && r.subject.EntityType == entityType);
        }

        if (query.EntityId is { } entityId)
        {
            rows = rows.Where(r => r.subject != null && r.subject.EntityId == entityId);
        }

        if (query.EventType is { } eventType)
        {
            rows = rows.Where(r => r.entry.EventType == eventType);
        }

        if (query.From is { } from)
        {
            rows = rows.Where(r => r.entry.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            rows = rows.Where(r => r.entry.OccurredAt <= to);
        }

        return await rows.OrderByDescending(r => r.entry.Seq).Take(query.Take)
            .Select(r => new AuditEntry(
                r.entry.Seq, r.entry.EventId, r.entry.EventType, r.entry.OccurredAt,
                r.subject == null ? null : r.subject.Actor, r.subject == null ? null : r.subject.EntityType,
                r.subject == null ? null : r.subject.EntityId, r.subject == null ? null : r.subject.SiteId, r.entry.Payload))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
