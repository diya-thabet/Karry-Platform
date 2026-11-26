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

/// <summary>
/// What separation of duties asks the module that owns a record type (ADR-0037 §4), implemented by that module for its
/// <see cref="RecordType"/>.
/// </summary>
public interface IReviewableRecords
{
    string RecordType { get; }

    /// <summary>
    /// Whether <paramref name="person"/> (a field account) operated <paramref name="assetId"/> at some time within
    /// <c>[from, to]</c> (a record without an end runs as long as the module says); such a person may not decide on it.
    /// </summary>
    Task<bool> OperatedAsync(Guid person, Guid assetId, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a controller's correction to <paramref name="version"/> of the record as a new version (US-P2-VER-04),
    /// in the caller's transaction: the fields by name with their new values as the records write them. Null when the
    /// record moved past that version meanwhile; a refusal when a field or a value is not acceptable.
    /// </summary>
    Task<CorrectionOutcome> CorrectAsync(
        DbConnection connection, DbTransaction transaction, Guid recordId, int version, IReadOnlyDictionary<string, string> fields, string reason, string? actor,
        CorrectionThresholds thresholds, CancellationToken cancellationToken);
}

/// <summary>
/// The four-eyes thresholds of the item's review policy (US-P2-VER-04 AC3, ADR-0037 §5): a correction beyond either
/// needs a second approver; how a record type measures them is its module's business.
/// </summary>
public sealed record CorrectionThresholds(decimal Hours, decimal Share);

/// <summary>One field changed by a correction, before and after, as the records write them.</summary>
public sealed record FieldChange(string Field, string? Before, string? After);

/// <summary>
/// What a correction produced: the new version and its hash, every changed field, and whether the change is beyond
/// the four-eyes thresholds (<c>Material</c>, US-P2-VER-04 AC3). Exactly one of the three outcomes.
/// </summary>
public abstract record CorrectionOutcome
{
    private CorrectionOutcome()
    {
    }

    public sealed record Corrected(int Version, string Hash, IReadOnlyList<FieldChange> Changes, bool Material) : CorrectionOutcome;

    /// <summary>The record has a newer version than the one the reviewer corrected.</summary>
    public sealed record Moved : CorrectionOutcome;

    /// <summary>A field that cannot be corrected, or a value that is not acceptable (<see cref="Code"/>).</summary>
    public sealed record Refused(string Code, string Message) : CorrectionOutcome;
}

/// <summary>The record types Verification reviews.</summary>
public static class ReviewRecordTypes
{
    /// <summary>A shift entry's ended version (FieldOps, ADR-0034).</summary>
    public const string ShiftEntry = "fieldops.shift_entry";
}

/// <summary>
/// A site's review work for a manager's daily digest (US-P2-VER-06 AC2): items waiting for a decision now, those of them
/// past their due time, and corrections made in the period.
/// </summary>
public sealed record ReviewCounts(int Pending, int Overdue, int Corrected);

/// <summary>The current company's review counts of one site (RLS), for the daily digest (ADR-0039 §4).</summary>
public interface IReviewCounts
{
    /// <summary>
    /// Pending and overdue at <paramref name="now"/>; corrections made from <paramref name="since"/> to
    /// <paramref name="now"/>.
    /// </summary>
    Task<ReviewCounts> OfSiteAsync(Guid siteId, DateTimeOffset since, DateTimeOffset now, CancellationToken cancellationToken);
}
