namespace Karry.Modules.Verification.Contracts;

/// <summary>
/// Where a record's review stands, for documents that print it (the shift report, ADR-0040): the status of its newest
/// reviewed version, and the last decision on it (who, when, why), if any. <c>DecidedBy</c> is the person's sign-in
/// subject; an auto-approval names nobody.
/// </summary>
public sealed record ReviewOutcome(Guid RecordId, int RecordVersion, string Status, string? DecidedBy, DateTimeOffset? DecidedAt, string? ReasonCode);

/// <summary>The current company's review outcomes of some records (RLS); records never submitted for review are left out.</summary>
public interface IReviewOutcomes
{
    Task<IReadOnlyList<ReviewOutcome>> OfRecordsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken);
}
