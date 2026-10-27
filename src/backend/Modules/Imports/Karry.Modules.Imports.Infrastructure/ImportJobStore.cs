using Karry.BuildingBlocks.Imports;
using Karry.Modules.Imports.Application;
using Karry.Modules.Imports.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Imports.Infrastructure;

internal sealed class ImportJobStore(ImportsDbContext db) : IImportJobStore
{
    public void Add(ImportJob job) => db.Jobs.Add(job);

    public Task<ImportJob?> FindAsync(ImportJobId id, CancellationToken cancellationToken) =>
        db.Jobs.SingleOrDefaultAsync(j => j.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ImportJob>> ListAsync(Guid? before, int take, CancellationToken cancellationToken)
    {
        // Job IDs are UUIDv7: ordering by ID is ordering by upload time. EF cannot compare a value-converted ID, so the
        // cursor condition is SQL (xmin, the concurrency token, is a system column that * does not return); the query
        // filter and row-level security still apply on top of it.
        var jobs = before is { } cursor ? db.Jobs.FromSql($"SELECT *, xmin FROM imports.jobs WHERE id < {cursor}") : db.Jobs;
        return await jobs.AsNoTracking().OrderByDescending(j => j.Id).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceReportAsync(ImportJob job, IReadOnlyList<ImportIssue> issues, IReadOnlyList<ImportRowPlan> plans, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(plans);
        await db.Issues.Where(i => i.JobId == job.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Rows.Where(r => r.JobId == job.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Issues.AddRange(issues.Select((issue, index) => new ImportJobIssue(
            job.TenantId, job.Id, index + 1, issue.Row, issue.Column, issue.Cell, issue.Code,
            issue.Severity == ImportSeverity.Error ? ImportJobIssue.Error : ImportJobIssue.Warning, issue.Args)));
        db.Rows.AddRange(plans.Select(plan => new ImportJobRow(job.TenantId, job.Id, plan.Row, plan.Key, plan.Action.ToString().ToLowerInvariant())));
    }

    public async Task<IReadOnlyList<ImportJobIssue>> IssuesAsync(ImportJobId id, string? severity, int after, int take, CancellationToken cancellationToken)
    {
        var issues = db.Issues.AsNoTracking().Where(i => i.JobId == id && i.Sequence > after);
        if (severity is not null)
        {
            issues = issues.Where(i => i.Severity == severity);
        }

        return await issues.OrderBy(i => i.Sequence).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImportJobRow>> RowsAsync(ImportJobId id, int after, int take, CancellationToken cancellationToken) =>
        await db.Rows.AsNoTracking().Where(r => r.JobId == id && r.Row > after).OrderBy(r => r.Row).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ImportJob>> FilesUploadedBeforeAsync(DateTimeOffset before, int take, CancellationToken cancellationToken) =>
        await db.Jobs.Where(j => j.FileDeletedAt == null && j.UploadedAt < before).OrderBy(j => j.UploadedAt).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
