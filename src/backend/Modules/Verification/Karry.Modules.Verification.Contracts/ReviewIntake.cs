using System.Data.Common;

namespace Karry.Modules.Verification.Contracts;

/// <summary>
/// A record version ready for review, as its owning module describes it (ADR-0037 §1): who produced it, the asset and
/// the time it covers, its flags, and the hash of the version as signed. Verification knows no field of the record.
/// </summary>
public sealed record ReviewSubmission(
    string RecordType, Guid RecordId, int RecordVersion, string RecordHash, Guid SiteId, Guid ProducedBy, Guid? AssetId,
    DateTimeOffset CoveredFrom, DateTimeOffset? CoveredTo, IReadOnlyList<string> Flags);

/// <summary>
/// How a module puts a record version up for review, in its own transaction so the version and its review item commit
/// together (as integrity flags do, ADR-0036 §3). Submitting the same version again changes nothing.
/// </summary>
public interface IReviewIntake
{
    Task SubmitAsync(DbConnection connection, DbTransaction transaction, ReviewSubmission submission, CancellationToken cancellationToken);
}

/// <summary>The record types Verification reviews.</summary>
public static class ReviewRecordTypes
{
    /// <summary>A shift entry's ended version (FieldOps, ADR-0034).</summary>
    public const string ShiftEntry = "fieldops.shift_entry";
}
