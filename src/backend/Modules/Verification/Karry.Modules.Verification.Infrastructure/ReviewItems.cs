using System.Data.Common;
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
        var items = db.Items.AsNoTracking();
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

        if (query.After is { } after)
        {
            var cursor = new ReviewItemId(after);
            items = items.Where(i => i.Id > cursor);
        }

        return await items.OrderBy(i => i.Id).Take(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class VerificationInfrastructure
{
    /// <summary>Review items (ADR-0037): their storage, the contract other modules submit through, and the queue's reading.</summary>
    public static IServiceCollection AddVerificationInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<VerificationDbContext>(connectionString);
        services.AddScoped<IReviewIntake, ReviewIntake>();
        services.AddScoped<IReviewItemReader, ReviewItemReader>();
        services.AddScoped<ReviewReading>();
        services.AddScoped<IReviewItemStore, ReviewItemStore>();
        services.AddScoped<ReviewDecisions>();
        services.AddScoped<ISyncSource, ReviewOutcomesSync>();
        services.AddScoped<IReviewPolicyStore, ReviewPolicyStore>();
        services.AddScoped<ReviewPolicies>();
        return services;
    }
}
