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
public sealed record ImportUploaded(Guid JobId, string Kind, string FileName, long Size, string Sha256, string? Actor) : DomainEvent;

[EventType("imports.import_validated.v1")]
public sealed record ImportValidated(Guid JobId, string Kind, int Rows, int Errors, int Warnings) : DomainEvent;

[EventType("imports.import_committed.v1")]
public sealed record ImportCommitted(Guid JobId, string Kind, int Created, int Updated, int Unchanged, string? Actor) : DomainEvent;

[EventType("imports.import_discarded.v1")]
public sealed record ImportDiscarded(Guid JobId, string Kind, string? Actor) : DomainEvent;

[EventType("imports.import_file_deleted.v1")]
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
    public Result<bool> Validated(TimeProvider clock, int rows, int errors, int warnings)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!IsQueued)
        {
            return ImportJobErrors.NotQueued;
        }

        (Rows, Errors, Warnings) = (rows, errors, warnings);
        Status = ImportJobStatus.Validated;
        ChangedAt = clock.GetUtcNow();
        Raise(new ImportValidated(Id.Value, Kind, rows, errors, warnings) { OccurredAt = ChangedAt });
        return true;
    }

    /// <summary>The user asks for the commit: only a validated file with no error.</summary>
    public Result<bool> RequestCommit(TimeProvider clock)
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
        ChangedAt = clock.GetUtcNow();
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
        Raise(new ImportCommitted(Id.Value, Kind, created, updated, unchanged, RequestedBy) { OccurredAt = ChangedAt });
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

public static class ImportJobErrors
{
    public static readonly Error FileNameInvalid = Error.Validation(
        "imports.file_name_invalid", $"A file name is 1 to {ImportJob.FileNameMaxLength} characters, without control characters.");

    public static readonly Error NotQueued = Error.Conflict("imports.job_not_queued", "The import is not waiting for this step.");

    public static readonly Error NotValidated = Error.Conflict("imports.job_not_validated", "Only a validated import can be committed.");

    public static readonly Error HasErrors = Error.BusinessRule("imports.job_has_errors", "Fix every error in the file before committing it.");

    public static readonly Error NotDiscardable = Error.Conflict("imports.job_not_discardable", "A committed import, or one being committed, cannot be discarded.");
}
