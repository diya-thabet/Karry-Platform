using System.Globalization;
using System.Security.Cryptography;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Storage;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Imports.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Imports.Application;

public interface IImportJobStore
{
    void Add(ImportJob job);

    Task<ImportJob?> FindAsync(ImportJobId id, CancellationToken cancellationToken);

    /// <summary>Newest first; <paramref name="before"/> is the ID of the last job of the previous page.</summary>
    Task<IReadOnlyList<ImportJob>> ListAsync(Guid? before, int take, CancellationToken cancellationToken);

    /// <summary>Replaces the job's issues and preview with those of the last validation.</summary>
    Task ReplaceReportAsync(ImportJob job, IReadOnlyList<ImportIssue> issues, IReadOnlyList<ImportRowPlan> plans, CancellationToken cancellationToken);

    /// <summary>Issues in file order after the sequence <paramref name="after"/>; only errors or warnings when <paramref name="severity"/> is given.</summary>
    Task<IReadOnlyList<ImportJobIssue>> IssuesAsync(ImportJobId id, string? severity, int after, int take, CancellationToken cancellationToken);

    /// <summary>The preview in row order after the row <paramref name="after"/>.</summary>
    Task<IReadOnlyList<ImportJobRow>> RowsAsync(ImportJobId id, int after, int take, CancellationToken cancellationToken);

    /// <summary>Jobs of the current tenant whose original file is still stored and was uploaded before <paramref name="before"/>.</summary>
    Task<IReadOnlyList<ImportJob>> FilesUploadedBeforeAsync(DateTimeOffset before, int take, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Malware scanning of uploaded files (ADR-0027 §6). Production plugs ClamAV in; where no scanner is configured,
/// <see cref="NoFileScanner"/> accepts every file and the job records <c>none</c>.
/// </summary>
public interface IFileScanner
{
    /// <summary>Recorded on the job and in the ledger.</summary>
    string Name { get; }

    Task<bool> IsCleanAsync(byte[] file, CancellationToken cancellationToken);
}

public sealed class NoFileScanner : IFileScanner
{
    public string Name => "none";

    public Task<bool> IsCleanAsync(byte[] file, CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>Where a job's original file is kept (ADR-0027 §9), and for how long.</summary>
public static class ImportFiles
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    public static string Key(ImportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ObjectKeys.ForTenant(job.TenantId.Value, "imports", $"{job.Id}/original.xlsx");
    }
}

/// <summary>Errors of import jobs (problem codes the apps translate), besides those of <see cref="ImportJobErrors"/>.</summary>
public static class ImportJobProblems
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static readonly Error NotFound = Error.NotFound("imports.job_not_found", "There is no such import.");

    public static readonly Error LimitInvalid = Error.Validation("imports.limit_invalid", $"limit is 1 to {MaxLimit}.");

    public static readonly Error CursorInvalid = Error.Validation("imports.cursor_invalid", "cursor is the nextCursor of the previous page.");

    public static readonly Error SeverityInvalid = Error.Validation("imports.severity_invalid", "severity is error or warning.");

    public static readonly Error FileMissing = Error.Validation("imports.file_missing", "Choose an Excel file to upload.");

    public static readonly Error StorageUnavailable = Error.BusinessRule("imports.storage_unavailable", "File storage is not configured on this server.");

    // File-level issues the worker records (row 0): the file is gone (retention), was refused by the scanner, or the
    // requester no longer holds the rights the import needs.
    public const string FileDeleted = "imports.file_deleted";
    public const string FileInfected = "imports.file_infected";
}

public sealed record ImportPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// Import jobs as the API sees them (US-P1-ORG-03 AC2–AC5, ADR-0027 §4): upload (the file is checked, stored and queued
/// for validation), the report and preview, commit and discard. The work itself is done by <see cref="ImportRunner"/>.
/// </summary>
public sealed class ImportJobOperations(
    ImportTargets targets, IWorkbookReader reader, IObjectStore files, IImportJobStore store, ICallerAccess caller, ITenantContext tenant, TimeProvider clock)
{
    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Import jobs need a tenant.");

    public async Task<Result<ImportJob>> UploadAsync(string? kind, string? fileName, byte[]? content, CancellationToken cancellationToken)
    {
        var target = targets.Find(kind);
        if (target is null)
        {
            return ImportErrors.KindUnknown;
        }

        if (!caller.Current.Has(target.Permission))
        {
            return ImportErrors.TargetForbidden;
        }

        if (content is null || content.Length == 0)
        {
            return ImportJobProblems.FileMissing;
        }

        if (reader.Check(content) is { IsSuccess: false } refused)
        {
            return refused.Error!;
        }

        if (!files.Available)
        {
            return ImportJobProblems.StorageUnavailable;
        }

        var job = ImportJob.Upload(clock, Tenant, target.Kind, fileName, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)), caller.Subject);
        if (!job.IsSuccess)
        {
            return job.Error!;
        }

        // The file first: a job is never queued without its file (a failed save leaves an object no job points to).
        await files.PutAsync(ImportFiles.Key(job.Value), content, ImportFiles.ContentType, cancellationToken).ConfigureAwait(false);
        store.Add(job.Value);
        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    public async Task<Result<ImportJob>> GetAsync(ImportJobId id, CancellationToken cancellationToken) =>
        await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is { } job ? job : ImportJobProblems.NotFound;

    public async Task<Result<ImportPage<ImportJob>>> ListAsync(string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (Limit(limit) is not { } take)
        {
            return ImportJobProblems.LimitInvalid;
        }

        Guid? before = null;
        if (cursor is not null)
        {
            if (!Guid.TryParse(cursor, out var parsed))
            {
                return ImportJobProblems.CursorInvalid;
            }

            before = parsed;
        }

        var jobs = await store.ListAsync(before, take + 1, cancellationToken).ConfigureAwait(false);
        return Page(jobs, take, job => job.Id.ToString());
    }

    public async Task<Result<ImportPage<ImportJobIssue>>> IssuesAsync(ImportJobId id, string? severity, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (severity is not (null or ImportJobIssue.Error or ImportJobIssue.Warning))
        {
            return ImportJobProblems.SeverityInvalid;
        }

        var paging = Paging(cursor, limit);
        if (!paging.IsSuccess)
        {
            return paging.Error!;
        }

        if (await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return ImportJobProblems.NotFound;
        }

        var issues = await store.IssuesAsync(id, severity, paging.Value.After, paging.Value.Take + 1, cancellationToken).ConfigureAwait(false);
        return Page(issues, paging.Value.Take, issue => issue.Sequence.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<Result<ImportPage<ImportJobRow>>> RowsAsync(ImportJobId id, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var paging = Paging(cursor, limit);
        if (!paging.IsSuccess)
        {
            return paging.Error!;
        }

        if (await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return ImportJobProblems.NotFound;
        }

        var rows = await store.RowsAsync(id, paging.Value.After, paging.Value.Take + 1, cancellationToken).ConfigureAwait(false);
        return Page(rows, paging.Value.Take, row => row.Row.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Queues the commit; the requester needs the kind's permission now, and again when the worker commits.</summary>
    public async Task<Result<ImportJob>> CommitAsync(ImportJobId id, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return ImportJobProblems.NotFound;
        }

        if (targets.Find(job.Kind) is not { } target || !caller.Current.Has(target.Permission))
        {
            return ImportErrors.TargetForbidden;
        }

        var requested = job.RequestCommit(clock, caller.Subject);
        if (!requested.IsSuccess)
        {
            return requested.Error!;
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    public async Task<Result<ImportJob>> DiscardAsync(ImportJobId id, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return ImportJobProblems.NotFound;
        }

        var discarded = job.Discard(clock, caller.Subject);
        if (!discarded.IsSuccess)
        {
            return discarded.Error!;
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    private static int? Limit(int? limit) => limit switch
    {
        null => ImportJobProblems.DefaultLimit,
        >= 1 and <= ImportJobProblems.MaxLimit => limit,
        _ => null,
    };

    private static Result<(int After, int Take)> Paging(string? cursor, int? limit)
    {
        var after = 0;
        if (cursor is not null && !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out after))
        {
            return ImportJobProblems.CursorInvalid;
        }

        return Limit(limit) is { } take ? (after, take) : ImportJobProblems.LimitInvalid;
    }

    private static ImportPage<T> Page<T>(IReadOnlyList<T> items, int take, Func<T, string> cursorOf) =>
        items.Count > take ? new ImportPage<T>([.. items.Take(take)], cursorOf(items[take - 1])) : new ImportPage<T>(items, null);
}

/// <summary>
/// The worker side of a job (ADR-0027 §4), run by the module's outbox in the job's tenant: validates an uploaded file,
/// and commits it when asked. It acts with the access the requester holds now (rights removed since the upload stop the
/// job), never writes business data while validating, and records every problem as an issue rather than failing:
/// a delivery that throws is retried, so the same file gives the same report. A repeated delivery of a step already done
/// does nothing.
/// </summary>
public sealed partial class ImportRunner(
    IImportJobStore store,
    ImportTargets targets,
    IWorkbookReader reader,
    IObjectStore files,
    IFileScanner scanner,
    IAccessResolver resolver,
    AuthorizationCatalog catalog,
    ICallerAccess caller,
    TimeProvider clock,
    ILogger<ImportRunner> logger) : IDomainEventSubscriber<ImportUploaded>, IDomainEventSubscriber<ImportCommitRequested>
{
    public async Task HandleAsync(ImportUploaded domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (await store.FindAsync(new ImportJobId(domainEvent.JobId), cancellationToken).ConfigureAwait(false) is { Status: ImportJobStatus.Validating } job)
        {
            await RunAsync(job, job.RequestedBy, commit: false, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task HandleAsync(ImportCommitRequested domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (await store.FindAsync(new ImportJobId(domainEvent.JobId), cancellationToken).ConfigureAwait(false) is { Status: ImportJobStatus.Committing } job)
        {
            await RunAsync(job, job.CommitRequestedBy, commit: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(ImportJob job, string? subject, bool commit, CancellationToken cancellationToken)
    {
        var access = subject is null
            ? CallerAccess.Nobody
            : CallerAccess.From(catalog, await resolver.AssignmentsAsync(job.TenantId, subject, cancellationToken).ConfigureAwait(false));
        caller.Set(access, subject, signedInAt: null);

        var issues = new List<ImportIssue>();
        IReadOnlyList<ImportRowPlan> plans = [];
        var rows = 0;
        ImportCommit? committed = null;
        var target = targets.Find(job.Kind);
        var allowed = target is not null && access.Has(ImportsPermissions.FilesManage) && access.Has(target.Permission);
        var file = allowed ? await files.GetAsync(ImportFiles.Key(job), cancellationToken).ConfigureAwait(false) : null;
        if (!allowed)
        {
            issues.Add(FileIssue(target is null ? ImportErrors.KindUnknown.Code : ImportErrors.TargetForbidden.Code));
        }
        else if (file is null)
        {
            issues.Add(FileIssue(ImportJobProblems.FileDeleted));
        }
        else if (!await scanner.IsCleanAsync(file.Content, cancellationToken).ConfigureAwait(false))
        {
            LogInfected(logger, job.Id.ToString(), scanner.Name);
            issues.Add(FileIssue(ImportJobProblems.FileInfected));
        }
        else
        {
            var read = reader.Read(file.Content, target!.Columns);
            if (!read.IsSuccess)
            {
                issues.Add(FileIssue(read.Error!.Code));
            }
            else
            {
                rows = read.Value.Rows.Count;
                issues.AddRange(read.Value.Issues);

                // The commit validates again inside its transaction; a file with errors of its own is only validated.
                var validation = commit && !read.Value.Issues.Any(i => i.Severity == ImportSeverity.Error)
                    ? (committed = await target.CommitAsync(read.Value.Rows, cancellationToken).ConfigureAwait(false)).Validation
                    : await target.ValidateAsync(read.Value.Rows, cancellationToken).ConfigureAwait(false);
                issues.AddRange(validation.Issues);
                plans = validation.Plans;
            }
        }

        var ordered = issues.OrderBy(i => i.Row).ThenBy(i => ColumnOrder(target, i.Column)).ToList();
        await store.ReplaceReportAsync(job, ordered, plans, cancellationToken).ConfigureAwait(false);
        if (committed is { Committed: true } done && !ordered.Any(i => i.Severity == ImportSeverity.Error))
        {
            job.Committed(clock, done.Created, done.Updated, done.Unchanged);
        }
        else
        {
            job.Validated(clock, rows, ordered.Count(i => i.Severity == ImportSeverity.Error), ordered.Count(i => i.Severity == ImportSeverity.Warning), scanner.Name);
        }

        await store.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ImportIssue FileIssue(string code) => new(0, null, null, code, ImportSeverity.Error);

    private static int ColumnOrder(IImportTarget? target, string? column)
    {
        var index = target is null || column is null ? -1 : target.Columns.ToList().FindIndex(c => c.Key == column);
        return index < 0 ? int.MaxValue : index;
    }

    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning, Message = "Import {Job}: the file was refused by the malware scanner {Scanner}")]
    private static partial void LogInfected(ILogger logger, string job, string scanner);
}

/// <summary>
/// Deletes the original files of imports older than 90 days (ADR-0027 §9), tenant by tenant, and records each deletion
/// in the company's ledger. The bucket's lifecycle rule is the second line in production.
/// </summary>
public sealed partial class DeleteExpiredImportFiles(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DeleteExpiredImportFiles> logger)
{
    private const int Batch = 200;

    public async Task<ImportRetentionRun> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var before = clock.GetUtcNow() - ImportFiles.Retention;
        var deleted = 0;
        var failed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                deleted += await DeleteForAsync(tenant, before, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogTenantFailed(logger, ex, tenant.ToString());
            }
        }

        LogRun(logger, tenants.Count, deleted, failed);
        return new ImportRetentionRun(tenants.Count, deleted, failed);
    }

    private async Task<int> DeleteForAsync(TenantId tenant, DateTimeOffset before, CancellationToken cancellationToken)
    {
        var deleted = 0;
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            services.GetRequiredService<ITenantContext>().Set(tenant);
            var store = services.GetRequiredService<IImportJobStore>();
            var files = services.GetRequiredService<IObjectStore>();
            var jobs = await store.FilesUploadedBeforeAsync(before, Batch, cancellationToken).ConfigureAwait(false);
            foreach (var job in jobs)
            {
                // Deleting an object that is already gone succeeds, so a run stopped half-way is simply repeated.
                await files.DeleteAsync(ImportFiles.Key(job), cancellationToken).ConfigureAwait(false);
                job.FileDeleted(clock);
            }

            await store.SaveAsync(cancellationToken).ConfigureAwait(false);
            deleted += jobs.Count;
            if (jobs.Count < Batch)
            {
                return deleted;
            }
        }
    }

    [LoggerMessage(EventId = 7102, Level = LogLevel.Error, Message = "Expired import files of tenant {Tenant} could not be deleted")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, string tenant);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Information, Message = "Import retention: {Tenants} tenants, {Deleted} file(s) deleted, {Failed} failed")]
    private static partial void LogRun(ILogger logger, int tenants, int deleted, int failed);
}

public sealed record ImportRetentionRun(int Tenants, int Deleted, int Failed);
