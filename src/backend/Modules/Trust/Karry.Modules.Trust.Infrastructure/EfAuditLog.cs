using System.Runtime.CompilerServices;
using Karry.Modules.Trust.Application;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Trust.Infrastructure;

internal sealed class EfAuditLog(TrustDbContext db) : IAuditLog
{
    public async Task<IReadOnlyList<AuditEntry>> SearchAsync(AuditQuery query, CancellationToken cancellationToken) =>
        await Entries(Filtered(query).OrderByDescending(r => r.Entry.Seq).Take(query.Take))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<AuditSummary> SummaryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var rows = Filtered(query);
        var count = await rows.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var newest = await rows.MaxAsync(r => (long?)r.Entry.Seq, cancellationToken).ConfigureAwait(false);
        return new AuditSummary(count, newest);
    }

    public async IAsyncEnumerable<AuditEntry> StreamAsync(AuditQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Streamed, not buffered: an export holds up to 100 000 entries.
        await foreach (var entry in Entries(Filtered(query).OrderByDescending(r => r.Entry.Seq)).AsAsyncEnumerable()
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    public async Task<IReadOnlyList<string>> ActorsAsync(AuditQuery query, CancellationToken cancellationToken) =>
        await Filtered(query).Where(r => r.Subject != null && r.Subject.Actor != null).Select(r => r.Subject!.Actor!)
            .Distinct().OrderBy(a => a).Take(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);

    private static IQueryable<AuditEntry> Entries(IQueryable<Row> rows) =>
        rows.Select(r => new AuditEntry(
            r.Entry.Seq, r.Entry.EventId, r.Entry.EventType, r.Entry.OccurredAt,
            r.Subject == null ? null : r.Subject.Actor, r.Subject == null ? null : r.Subject.EntityType,
            r.Subject == null ? null : r.Subject.EntityId, r.Subject == null ? null : r.Subject.SiteId, r.Entry.Payload));

    private IQueryable<Row> Filtered(AuditQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Left join: an entry of an event type this version does not know has no subject; it shows only unfiltered
        // and to company-wide readers.
        var rows =
            from entry in db.LedgerEntries.AsNoTracking()
            join indexed in db.LedgerSubjects.AsNoTracking() on entry.Seq equals indexed.Seq into subjects
            from subject in subjects.DefaultIfEmpty()
            select new Row { Entry = entry, Subject = subject };

        if (query.Before is { } before)
        {
            rows = rows.Where(r => r.Entry.Seq < before);
        }

        if (query.Sites is { } sites)
        {
            rows = rows.Where(r => r.Subject != null && r.Subject.SiteId != null && sites.Contains(r.Subject.SiteId.Value));
        }

        if (query.SiteId is { } site)
        {
            rows = rows.Where(r => r.Subject != null && r.Subject.SiteId == site);
        }

        if (query.Actor is { } actor)
        {
            rows = rows.Where(r => r.Subject != null && r.Subject.Actor == actor);
        }

        if (query.EntityType is { } entityType)
        {
            rows = rows.Where(r => r.Subject != null && r.Subject.EntityType == entityType);
        }

        if (query.EntityId is { } entityId)
        {
            rows = rows.Where(r => r.Subject != null && r.Subject.EntityId == entityId);
        }

        if (query.EventType is { } eventType)
        {
            rows = rows.Where(r => r.Entry.EventType == eventType);
        }

        if (query.From is { } from)
        {
            rows = rows.Where(r => r.Entry.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            rows = rows.Where(r => r.Entry.OccurredAt <= to);
        }

        return rows;
    }

    private sealed class Row
    {
        public required LedgerEntryRow Entry { get; init; }

        public LedgerSubjectRow? Subject { get; init; }
    }
}
