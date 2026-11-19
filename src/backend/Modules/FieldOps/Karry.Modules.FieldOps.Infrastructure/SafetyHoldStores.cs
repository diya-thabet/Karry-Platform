using Karry.BuildingBlocks.Sync;
using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karry.Modules.FieldOps.Infrastructure;

internal sealed class ChecklistSubmissionStore(FieldOpsDbContext db) : IChecklistSubmissionStore
{
    public Task JoinAsync(ISyncTransaction transaction, CancellationToken cancellationToken) => db.JoinAsync(transaction, cancellationToken);

    public Task<ChecklistTemplate?> TemplateAsync(ChecklistTemplateId id, CancellationToken cancellationToken) =>
        db.ChecklistTemplates.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(Guid rootId, CancellationToken cancellationToken) => db.ChecklistSubmissions.AnyAsync(s => s.RootId == rootId, cancellationToken);

    public void Add(ChecklistSubmission submission, SafetyHold? hold)
    {
        db.ChecklistSubmissions.Add(submission);
        if (hold is not null)
        {
            db.SafetyHolds.Add(hold);
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

internal sealed class SafetyHoldStore(FieldOpsDbContext db) : ISafetyHoldStore
{
    public Task<SafetyHold?> FindAsync(SafetyHoldId id, CancellationToken cancellationToken) =>
        db.SafetyHolds.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);

    public Task<bool> ReleasedAsync(SafetyHoldId id, CancellationToken cancellationToken) => db.SafetyHoldReleases.AnyAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<(SafetyHold Hold, SafetyHoldRelease? Release)>> SearchAsync(HoldQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var holds = db.SafetyHolds.AsNoTracking();
        if (query.Sites is { } sites)
        {
            var allowed = sites.ToList();
            holds = holds.Where(h => allowed.Contains(h.SiteId));
        }

        if (query.SiteId is { } site)
        {
            holds = holds.Where(h => h.SiteId == site);
        }

        var rows = from h in holds
                   join r in db.SafetyHoldReleases.AsNoTracking() on h.Id equals r.Id into releases
                   from r in releases.DefaultIfEmpty()
                   select new { Hold = h, Release = r };
        if (query.Released is { } released)
        {
            rows = released ? rows.Where(x => x.Release != null) : rows.Where(x => x.Release == null);
        }

        var found = await rows.OrderByDescending(x => x.Hold.OpenedAt).Take(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. found.Select(x => (x.Hold, (SafetyHoldRelease?)x.Release))];
    }

    public async Task<bool> TryAddAsync(SafetyHoldRelease release, CancellationToken cancellationToken)
    {
        db.SafetyHoldReleases.Add(release);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
