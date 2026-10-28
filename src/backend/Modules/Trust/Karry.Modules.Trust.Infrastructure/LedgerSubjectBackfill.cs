using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Application;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>
/// One tenant at a time (row-level security applies), in batches. Entries of an event type this version does not know
/// are left without a subject.
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
                        db.LedgerSubjects.Add(new LedgerSubjectRow
                        {
                            TenantId = tenant,
                            Seq = entry.Seq,
                            Actor = subject.Actor,
                            EntityType = subject.Entity,
                            EntityId = subject.EntityId,
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
}
