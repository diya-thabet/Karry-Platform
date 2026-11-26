using System.Data.Common;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Verification.Application;
using Karry.Modules.Verification.Contracts;
using Karry.Modules.Verification.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Verification.Infrastructure;


/// <summary>Records' review outcomes for documents (ADR-0040): the newest item of each record and its last decision (RLS).</summary>
internal sealed class ReviewOutcomesReader(VerificationDbContext db) : IReviewOutcomes
{
    /// <summary>The actions that decide an item; an escalation does not.</summary>
    private static readonly string[] Decisions =
        [ReviewActionKinds.Approve, ReviewActionKinds.Reject, ReviewActionKinds.Correct, ReviewActionKinds.SecondApprove, ReviewActionKinds.AutoApprove];

    public async Task<IReadOnlyList<ReviewOutcome>> OfRecordsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0)
        {
            return [];
        }

        var wanted = recordIds.ToList();
        var items = (await db.Items.AsNoTracking().Where(i => wanted.Contains(i.RecordId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(i => i.RecordId).Select(g => g.OrderByDescending(i => i.RecordVersion).ThenByDescending(i => i.CreatedAt).First()).ToList();
        var itemIds = items.Select(i => i.Id).ToList();
        var last = (await db.Actions.AsNoTracking().Where(a => itemIds.Contains(a.ItemId) && Decisions.Contains(a.Kind)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.At).First());
        return [.. items.Select(i => last.GetValueOrDefault(i.Id) is { } decision
            ? new ReviewOutcome(i.RecordId, i.RecordVersion, i.Status, decision.Actor, decision.At, decision.ReasonCode)
            : new ReviewOutcome(i.RecordId, i.RecordVersion, i.Status, null, null, null))];
    }
}

/// <summary>A site's review counts for the daily digest (US-P2-VER-06 AC2): read in the company's unit of work (RLS).</summary>
internal sealed class ReviewCountsReader(VerificationDbContext db) : IReviewCounts
{
    public async Task<ReviewCounts> OfSiteAsync(Guid siteId, DateTimeOffset since, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var site = db.Items.AsNoTracking().Where(i => i.SiteId == siteId);
        var waiting = site.Where(i => i.Status == ReviewStatuses.Pending || i.Status == ReviewStatuses.AwaitingSecond);
        var pending = await waiting.CountAsync(cancellationToken).ConfigureAwait(false);
        var overdue = await waiting.CountAsync(i => i.DueAt < now, cancellationToken).ConfigureAwait(false);
        // A correction is a new item for the corrected version, created when the controller corrected it.
        var corrected = await site.CountAsync(i => i.CorrectedBy != null && i.CreatedAt >= since && i.CreatedAt < now, cancellationToken).ConfigureAwait(false);
        return new ReviewCounts(pending, overdue, corrected);
    }
}

/// <summary>
/// Puts record versions up for review for the other modules (ADR-0037 §1), joined to their transaction: the version,
/// its review item and the item's ledger entry commit together.
/// </summary>
internal sealed class ReviewIntake(VerificationDbContext db, ITenantContext tenant, IReviewPolicyStore policies, TimeProvider clock) : IReviewIntake
{
    public async Task SubmitAsync(DbConnection connection, DbTransaction transaction, ReviewSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var company = tenant.Current ?? throw new InvalidOperationException("Review items are submitted in a tenant.");
        await db.JoinAsync(new Joined(connection, transaction), cancellationToken).ConfigureAwait(false);
        var known = await db.Items.AnyAsync(
            i => i.RecordType == submission.RecordType && i.RecordId == submission.RecordId && i.RecordVersion == submission.RecordVersion, cancellationToken)
            .ConfigureAwait(false);
        if (known)
        {
            return;
        }

        var item = ReviewItem.Submit(
            clock, company, submission.RecordType, submission.RecordId, submission.RecordVersion, submission.RecordHash, submission.SiteId,
            submission.ProducedBy, submission.AssetId, submission.CoveredFrom, submission.CoveredTo, submission.Flags);

        // The policy in force now is the item's for good (TC-P2-VER-007); it may approve the item at once (TC-P2-VER-006).
        var policy = await policies.InForceAsync(submission.RecordType, item.CreatedAt, cancellationToken).ConfigureAwait(false);
        db.Items.Add(item);
        if (item.Pin(clock, policy) is { } approved)
        {
            db.Actions.Add(approved);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record Joined(DbConnection Connection, DbTransaction Transaction) : ISyncTransaction;
}

internal sealed class ReviewItemStore(VerificationDbContext db) : IReviewItemStore
{
    public Task<ReviewItem?> FindAsync(ReviewItemId id, CancellationToken cancellationToken) =>
        db.Items.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ReviewItem>> FindManyAsync(IReadOnlyCollection<ReviewItemId> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToList();
        return await db.Items.Where(i => wanted.Contains(i.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Add(ReviewAction action) => db.Actions.Add(action);

    public void Add(ReviewBatch batch) => db.Batches.Add(batch);

    public void Add(ReviewItem item) => db.Items.Add(item);

    public async Task<(DbConnection Connection, DbTransaction Transaction)> BeginAsync(CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return (db.Database.GetDbConnection(), transaction.GetDbTransaction());
    }

    public Task CommitAsync(CancellationToken cancellationToken) => db.Database.CommitTransactionAsync(cancellationToken);

    public async Task<IReadOnlyList<ReviewItem>> OverdueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        string[] waiting = [ReviewStatuses.Pending, ReviewStatuses.AwaitingSecond];
        return await db.Items.Where(i => waiting.Contains(i.Status) && i.EscalatedAt == null && i.DueAt <= now)
            .OrderBy(i => i.DueAt).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

internal sealed class ReviewPolicyStore(VerificationDbContext db) : IReviewPolicyStore
{
    public async Task<EffectivePolicy> InForceAsync(string recordType, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var policy = await db.Policies.AsNoTracking().Where(p => p.RecordType == recordType && p.EffectiveFrom <= at)
            .OrderByDescending(p => p.Version).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return policy is null ? EffectivePolicy.Defaults : new EffectivePolicy(policy.Version, policy.Terms, policy.PublishedBy);
    }

    public async Task<EffectivePolicy> VersionAsync(string recordType, int version, CancellationToken cancellationToken)
    {
        var policy = version == 0 ? null : await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.RecordType == recordType && p.Version == version, cancellationToken)
            .ConfigureAwait(false);
        return policy is null ? EffectivePolicy.Defaults : new EffectivePolicy(policy.Version, policy.Terms, policy.PublishedBy);
    }

    public async Task<IReadOnlyList<ReviewPolicy>> ListAsync(CancellationToken cancellationToken) =>
        await db.Policies.AsNoTracking().OrderBy(p => p.RecordType).ThenBy(p => p.Version).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> TryAddAsync(ReviewPolicy policy, CancellationToken cancellationToken)
    {
        db.Policies.Add(policy);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

internal sealed class ReviewItemReader(VerificationDbContext db) : IReviewItemReader
{
    public async Task<IReadOnlyList<ReviewItem>> SearchAsync(ReviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // A flag is an element of the jsonb array: no LINQ for it behind the value converter (RLS still applies).
        var items = query.Flag is { } flag
            ? db.Items.FromSql($"SELECT i.*, i.xmin FROM verification.review_items i WHERE jsonb_exists(i.flags, {flag})").AsNoTracking()
            : db.Items.AsNoTracking();
        if (query.Sites is { } sites)
        {
            var allowed = sites.ToList();
            items = items.Where(i => allowed.Contains(i.SiteId));
        }

        if (query.SiteId is { } site)
        {
            items = items.Where(i => i.SiteId == site);
        }

        var statuses = query.Statuses.ToList();
        items = items.Where(i => statuses.Contains(i.Status));
        if (query.RecordType is { } type)
        {
            items = items.Where(i => i.RecordType == type);
        }

        if (query.OverdueOnly)
        {
            items = items.Where(i => i.EscalatedAt != null);
        }

        if (query.AssetId is { } asset)
        {
            items = items.Where(i => i.AssetId == asset);
        }

        if (query.ProducedBy is { } producer)
        {
            items = items.Where(i => i.ProducedBy == producer);
        }

        if (query.CoveredFrom is { } from)
        {
            items = items.Where(i => i.CoveredFrom >= from);
        }

        if (query.CoveredBefore is { } before)
        {
            items = items.Where(i => i.CoveredFrom < before);
        }

        if (query.After is { } after)
        {
            var cursor = new ReviewItemId(after);
            // By risk, the next page starts after the cursor item as it ranks now (an item escalated meanwhile moves up).
            var risk = query.ByRisk
                ? await db.Items.AsNoTracking().Where(i => i.Id == cursor).Select(i => (int?)i.Risk).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                : null;
            items = risk is { } rank ? items.Where(i => i.Risk < rank || (i.Risk == rank && i.Id > cursor)) : items.Where(i => i.Id > cursor);
        }

        var ordered = query.ByRisk ? items.OrderByDescending(i => i.Risk).ThenBy(i => i.Id) : items.OrderBy(i => i.Id);
        return await ordered.Take(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class VerificationInfrastructure
{
    /// <summary>Review items (ADR-0037): their storage, the contract other modules submit through, and the queue's reading.</summary>
    public static IServiceCollection AddVerificationInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<VerificationDbContext>(connectionString);
        // Its events reach other modules (notices, ADR-0039): dispatched by the Workers.
        services.AddOutboxDispatcher<VerificationDbContext>(eventAssemblies: typeof(Domain.ReviewItem).Assembly);
        services.AddScoped<IReviewIntake, ReviewIntake>();
        services.AddScoped<IReviewCounts, ReviewCountsReader>();
        services.AddScoped<IReviewOutcomes, ReviewOutcomesReader>();
        services.AddScoped<IReviewItemReader, ReviewItemReader>();
        services.AddScoped<ReviewReading>();
        services.AddScoped<ReviewNotices>();
        services.AddScoped<BuildingBlocks.Outbox.IDomainEventSubscriber<Domain.ReviewItemRejected>>(sp => sp.GetRequiredService<ReviewNotices>());
        services.AddScoped<BuildingBlocks.Outbox.IDomainEventSubscriber<Domain.ReviewItemEscalated>>(sp => sp.GetRequiredService<ReviewNotices>());
        services.AddScoped<IReviewItemStore, ReviewItemStore>();
        services.AddScoped<ReviewDecisions>();
        services.AddScoped<ISyncSource, ReviewOutcomesSync>();
        services.AddScoped<IReviewPolicyStore, ReviewPolicyStore>();
        services.AddScoped<ReviewPolicies>();
        services.AddSingleton<EscalateOverdueReviews>();
        return services;
    }
}
