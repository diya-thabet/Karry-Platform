using Karry.Modules.Imports.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Imports.Tests;

/// <summary>An import job's life (ADR-0027 §4, §9): what each step allows and what it records in the ledger.</summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class ImportJobTests
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Upload_validate_commit_records_each_step_with_its_counts()
    {
        var job = Upload();
        job.Status.Should().Be(ImportJobStatus.Validating);
        job.IsQueued.Should().BeTrue();
        job.FileName.Should().Be("flotte.xlsx", "only the name, never the client's folders");

        job.Validated(clock, rows: 60, errors: 0, warnings: 2, "none").IsSuccess.Should().BeTrue();
        job.RequestCommit(clock, "admin-3").IsSuccess.Should().BeTrue();
        job.Status.Should().Be(ImportJobStatus.Committing);
        job.CommitRequestedBy.Should().Be("admin-3", "the commit runs with the access of whoever asked for it");
        job.Committed(clock, created: 58, updated: 1, unchanged: 1).IsSuccess.Should().BeTrue();

        job.Status.Should().Be(ImportJobStatus.Committed);
        (job.Created, job.Updated, job.Unchanged).Should().Be((58, 1, 1));
        job.DomainEvents.Should().SatisfyRespectively(
            e => e.Should().BeEquivalentTo(new { JobId = job.Id.Value, Kind = "assets", FileName = "flotte.xlsx", Size = 2048L, Sha256 = "ab12", Actor = "admin-1" }),
            e => e.Should().BeOfType<ImportValidated>().Which.Should().Match<ImportValidated>(v => v.Rows == 60 && v.Errors == 0 && v.Warnings == 2 && v.Scanner == "none"),
            e => e.Should().BeOfType<ImportCommitRequested>().Which.Actor.Should().Be("admin-3"),
            e => e.Should().BeOfType<ImportCommitted>().Which.Actor.Should().Be("admin-3"));
    }

    [Fact]
    public void A_file_with_errors_cannot_be_committed_and_a_commit_that_finds_errors_returns_to_validated()
    {
        var job = Upload();
        job.RequestCommit(clock, "admin-1").Error!.Code.Should().Be("imports.job_not_validated");
        job.Validated(clock, 60, errors: 3, warnings: 0, "none");
        job.RequestCommit(clock, "admin-1").Error!.Code.Should().Be("imports.job_has_errors");

        job.Validated(clock, 60, 0, 0, "none").Error!.Code.Should().Be("imports.job_not_queued", "a validated job is not waiting for a worker");
        job.Committed(clock, 1, 0, 0).Error!.Code.Should().Be("imports.job_not_queued");

        var clean = Upload();
        clean.Validated(clock, 60, 0, 0, "none");
        clean.RequestCommit(clock, "admin-1");
        clean.Validated(clock, 60, errors: 1, warnings: 0, "none").IsSuccess.Should().BeTrue("the data changed since the preview");
        clean.Status.Should().Be(ImportJobStatus.Validated);
        clean.Errors.Should().Be(1);
    }

    [Fact]
    public void Only_a_job_that_is_not_committed_or_committing_can_be_discarded()
    {
        var job = Upload();
        job.Discard(clock, "admin-2").IsSuccess.Should().BeTrue();
        job.Status.Should().Be(ImportJobStatus.Discarded);
        job.DomainEvents.OfType<ImportDiscarded>().Single().Actor.Should().Be("admin-2");
        job.Discard(clock, "admin-2").Error!.Code.Should().Be("imports.job_not_discardable");

        var committing = Upload();
        committing.Validated(clock, 1, 0, 0, "none");
        committing.RequestCommit(clock, null);
        committing.Discard(clock, null).Error!.Code.Should().Be("imports.job_not_discardable");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("a\u0000.xlsx")]
    [InlineData("C:/Users/x/")]
    public void A_file_needs_a_sane_name(string? name) =>
        ImportJob.Upload(clock, Tenant, "assets", name, 1, "ab12", null).Error!.Code.Should().Be("imports.file_name_invalid");

    [Fact]
    public void The_file_deletion_is_recorded_once()
    {
        var job = Upload();
        clock.Advance(TimeSpan.FromDays(91));

        job.FileDeleted(clock).Should().BeTrue();
        job.FileDeleted(clock).Should().BeFalse();
        job.FileDeletedAt.Should().Be(clock.GetUtcNow());
        job.DomainEvents.OfType<ImportFileDeleted>().Should().ContainSingle().Which.Sha256.Should().Be("ab12");
    }

    private static readonly TenantId Tenant = new(Guid.CreateVersion7());

    private ImportJob Upload() => ImportJob.Upload(clock, Tenant, "assets", "  C:\\fakepath\\flotte.xlsx ", 2048, "ab12", "admin-1").Value;
}
