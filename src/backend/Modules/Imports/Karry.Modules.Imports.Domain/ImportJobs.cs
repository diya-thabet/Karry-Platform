using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Imports.Domain;

public readonly record struct ImportJobId(Guid Value) : IEntityId<ImportJobId>
{
    public static ImportJobId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

/// <summary>Where a job is (ADR-0027 §4). <c>Validating</c> and <c>Committing</c> wait for a worker.</summary>
public enum ImportJobStatus
{
    Validating,
    Validated,
    Committing,
    Committed,
    Discarded,
}

[EventType("imports.import_uploaded.v1")]
[AuditSubject("import", nameof(JobId))]
public sealed record ImportUploaded(Guid JobId, string Kind, string FileName, long Size, string Sha256, string? Actor) : DomainEvent;

/// <summary><c>Scanner</c>: the malware scanner that checked the file (<c>none</c> where no scanner is configured).</summary>
[EventType("imports.import_validated.v1")]
[AuditSubject("import", nameof(JobId))]
public sealed record ImportValidated(Guid JobId, string Kind, int Rows, int Errors, int Warnings, string Scanner) : DomainEvent;

[EventType("imports.import_commit_requested.v1")]
[AuditSubject("import", nameof(JobId))]
public sealed record ImportCommitRequested(Guid JobId, string Kind, string? Actor) : DomainEvent;

[EventType("imports.import_committed.v1")]
[AuditSubject("import", nameof(JobId))]
public sealed record ImportCommitted(Guid JobId, string Kind, int Created, int Updated, int Unchanged, string? Actor) : DomainEvent;

[EventType("imports.import_discarded.v1")]
[AuditSubject("import", nameof(JobId))]
public sealed record ImportDiscarded(Guid JobId, string Kind, string? Actor) : DomainEvent;

[EventType("imports.import_file_deleted.v1")]
[AuditSubject("import", nameof(JobId))]
public sealed record ImportFileDeleted(Guid JobId, string Kind, string Sha256) : DomainEvent;

/// <summary>
/// One uploaded file of one kind, from upload to commit or discard (US-P1-ORG-03, ADR-0027 §4 and §9). The worker
/// validates it (never writing business data) and commits it when asked; a commit that finds an error returns the job to
/// <see cref="ImportJobStatus.Validated"/> with the new counts. The original file is kept 90 days, then deleted.
/// Every step is in the company's ledger.
/// </summary>
public sealed class ImportJob : AggregateRoot<ImportJobId>, ITenantOwned
{
    public const int FileNameMaxLength = 255;

    private ImportJob(ImportJobId id, TenantId tenantId, string kind, string fileName, long size, string sha256, string? requestedBy)
        : base(id)
    {
        TenantId = tenantId;
        Kind = kind;
        FileName = fileName;
        Size = size;
        Sha256 = sha256;
        RequestedBy = requestedBy;
    }

    public TenantId TenantId { get; private set; }

    public string Kind { get; private set; }

    public string FileName { get; private set; }

    public long Size { get; private set; }

    public string Sha256 { get; private set; }

    /// <summary>The token subject of the uploader: the job runs with this user's access as it is when the job runs.</summary>
    public string? RequestedBy { get; private set; }

    public ImportJobStatus Status { get; private set; }

    /// <summary>The token subject of the user who asked for the commit: the commit runs with their access.</summary>
    public string? CommitRequestedBy { get; private set; }

    /// <summary>The malware scanner that checked the file at the last validation.</summary>
    public string? Scanner { get; private set; }

    public int Rows { get; private set; }

    public int Errors { get; private set; }

    public int Warnings { get; private set; }

    public int Created { get; private set; }

    public int Updated { get; private set; }

    public int Unchanged { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public DateTimeOffset? FileDeletedAt { get; private set; }

    /// <summary>Waiting for a worker (validation or commit).</summary>
    public bool IsQueued => Status is ImportJobStatus.Validating or ImportJobStatus.Committing;

    public static Result<ImportJob> Upload(TimeProvider clock, TenantId tenant, string kind, string? fileName, long size, string sha256, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        // Only the last segment, whichever separator the client used (browsers send C:\fakepath\name.xlsx).
        var trimmed = fileName?.Trim() ?? string.Empty;
        var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.Length is 0 or > FileNameMaxLength || name.Any(char.IsControl))
        {
            return ImportJobErrors.FileNameInvalid;
        }

        var at = clock.GetUtcNow();
        var job = new ImportJob(EntityId.New<ImportJobId>(clock), tenant, kind, name, size, sha256, actor)
        {
            Status = ImportJobStatus.Validating,
            UploadedAt = at,
            ChangedAt = at,
        };
        job.Raise(new ImportUploaded(job.Id.Value, kind, name, size, sha256, actor) { OccurredAt = at });
        return job;
    }

    /// <summary>The worker validated the file (first time, or a commit that found errors).</summary>
    public Result<bool> Validated(TimeProvider clock, int rows, int errors, int warnings, string scanner)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanner);
        if (!IsQueued)
        {
            return ImportJobErrors.NotQueued;
        }

        (Rows, Errors, Warnings, Scanner) = (rows, errors, warnings, scanner);
        Status = ImportJobStatus.Validated;
        ChangedAt = clock.GetUtcNow();
        Raise(new ImportValidated(Id.Value, Kind, rows, errors, warnings, scanner) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>The user asks for the commit: only a validated file with no error. The worker commits it with their access.</summary>
    public Result<bool> RequestCommit(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != ImportJobStatus.Validated)
        {
            return ImportJobErrors.NotValidated;
        }

        if (Errors > 0)
        {
            return ImportJobErrors.HasErrors;
        }

        Status = ImportJobStatus.Committing;
        CommitRequestedBy = actor;
        ChangedAt = clock.GetUtcNow();
        Raise(new ImportCommitRequested(Id.Value, Kind, actor) { OccurredAt = ChangedAt });
        return true;
    }

    public Result<bool> Committed(TimeProvider clock, int created, int updated, int unchanged)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != ImportJobStatus.Committing)
        {
            return ImportJobErrors.NotQueued;
        }

        (Created, Updated, Unchanged) = (created, updated, unchanged);
        Status = ImportJobStatus.Committed;
        ChangedAt = clock.GetUtcNow();
        Raise(new ImportCommitted(Id.Value, Kind, created, updated, unchanged, CommitRequestedBy) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>Drops a job that is not committed and not being committed.</summary>
    public Result<bool> Discard(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status is ImportJobStatus.Committing or ImportJobStatus.Committed or ImportJobStatus.Discarded)
        {
            return ImportJobErrors.NotDiscardable;
        }

        Status = ImportJobStatus.Discarded;
        ChangedAt = clock.GetUtcNow();
        Raise(new ImportDiscarded(Id.Value, Kind, actor) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>The retention job deleted the original; false when it was already gone.</summary>
    public bool FileDeleted(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (FileDeletedAt is not null)
        {
            return false;
        }

        FileDeletedAt = clock.GetUtcNow();
        Raise(new ImportFileDeleted(Id.Value, Kind, Sha256) { OccurredAt = FileDeletedAt.Value });
        return true;
    }
}

/// <summary>
/// A problem of a job's file as last validated (US-P1-ORG-03 AC2): row (0 for the file, 1 for the headers), column key,
/// cell, code, <c>error</c> or <c>warning</c>, arguments. Replaced at each validation.
/// </summary>
public sealed class ImportJobIssue(TenantId tenantId, ImportJobId jobId, int sequence, int row, string? column, string? cell, string code, string severity,
    IReadOnlyDictionary<string, string>? args) : ITenantOwned
{
    public const string Error = "error";
    public const string Warning = "warning";

    public TenantId TenantId { get; private set; } = tenantId;

    public ImportJobId JobId { get; private set; } = jobId;

    /// <summary>Order within the job (rows first, then columns), the paging key.</summary>
    public int Sequence { get; private set; } = sequence;

    public int Row { get; private set; } = row;

    public string? Column { get; private set; } = column;

    public string? Cell { get; private set; } = cell;

    public string Code { get; private set; } = code;

    public string Severity { get; private set; } = severity;

    public IReadOnlyDictionary<string, string>? Args { get; private set; } = args;
}

/// <summary>The preview of a row without an error (US-P1-ORG-03 AC3): its key and what a commit would do (<c>create</c>, <c>update</c>, <c>unchanged</c>).</summary>
public sealed class ImportJobRow(TenantId tenantId, ImportJobId jobId, int row, string key, string action) : ITenantOwned
{
    public TenantId TenantId { get; private set; } = tenantId;

    public ImportJobId JobId { get; private set; } = jobId;

    public int Row { get; private set; } = row;

    public string Key { get; private set; } = key;

    public string Action { get; private set; } = action;
}

public static class ImportJobErrors
{
    public static readonly Error FileNameInvalid = Error.Validation(
        "imports.file_name_invalid", $"A file name is 1 to {ImportJob.FileNameMaxLength} characters, without control characters.");

    public static readonly Error NotQueued = Error.Conflict("imports.job_not_queued", "The import is not waiting for this step.");

    public static readonly Error NotValidated = Error.Conflict("imports.job_not_validated", "Only a validated import can be committed.");

    public static readonly Error HasErrors = Error.BusinessRule("imports.job_has_errors", "Fix every error in the file before committing it.");

    public static readonly Error NotDiscardable = Error.Conflict("imports.job_not_discardable", "A committed import, or one being committed, cannot be discarded.");
}
