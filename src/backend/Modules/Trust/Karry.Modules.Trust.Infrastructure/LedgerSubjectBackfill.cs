using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Application;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>
/// One tenant at a time (row-level security applies), in batches, in sequence order (so a site inherited from an
/// earlier entry of the same entity is known). Entries of an event type this version does not know are left without a
/// subject.
/// </summary>
internal sealed class LedgerSubjectBackfill(IServiceScopeFactory scopes, EventSerializer events) : ILedgerSubjectBackfill
{
    private const int Batch = 500;

    public async Task<int> RunAsync(IEnumerable<TenantId> tenants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var added = 0;
        foreach (var tenant in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
            var db = scope.ServiceProvider.GetRequiredService<TrustDbContext>();
            var sites = new Dictionary<(string Entity, string Id), Guid>(); // latest site per entity, this run
            for (long after = 0; ;)
            {
                var entries = await db.LedgerEntries.AsNoTracking()
                    .Where(e => e.Seq > after && !db.LedgerSubjects.Any(s => s.Seq == e.Seq))
                    .OrderBy(e => e.Seq).Take(Batch)
                    .Select(e => new { e.Seq, e.EventType, e.Payload })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (entries.Count == 0)
                {
                    break;
                }

                foreach (var entry in entries)
                {
                    if (events.Deserialize(entry.EventType, entry.Payload) is { } domainEvent)
                    {
                        var subject = AuditSubject.Of(domainEvent);
                        var site = subject.SiteId ?? await LatestSiteAsync(db, sites, subject, cancellationToken).ConfigureAwait(false);
                        if (site is { } known && subject.EntityId is { } entityId)
                        {
                            sites[(subject.Entity, entityId)] = known;
                        }

                        db.LedgerSubjects.Add(new LedgerSubjectRow
                        {
                            TenantId = tenant,
                            Seq = entry.Seq,
                            Actor = subject.Actor,
                            EntityType = subject.Entity,
                            EntityId = subject.EntityId,
                            SiteId = site,
                        });
                        added++;
                    }
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                after = entries[^1].Seq;
            }
        }

        return added;
    }

    /// <summary>Like the ledger writer: an entry without a site takes its entity's latest known one.</summary>
    private static async Task<Guid?> LatestSiteAsync(TrustDbContext db, Dictionary<(string Entity, string Id), Guid> sites,
        AuditSubject subject, CancellationToken cancellationToken)
    {
        if (subject.EntityId is not { } entityId)
        {
            return null;
        }

        if (sites.TryGetValue((subject.Entity, entityId), out var known))
        {
            return known;
        }

        return await db.LedgerSubjects.AsNoTracking()
            .Where(s => s.EntityType == subject.Entity && s.EntityId == entityId && s.SiteId != null)
            .OrderByDescending(s => s.Seq).Select(s => s.SiteId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }
}
