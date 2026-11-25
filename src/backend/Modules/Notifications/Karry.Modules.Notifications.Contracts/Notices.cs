using System.Data.Common;

namespace Karry.Modules.Notifications.Contracts;

/// <summary>The kinds of notice of Phase 2 (ADR-0039 §2); the Notifications module knows who hears of each.</summary>
public static class NoticeTypes
{
    /// <summary>An entry was rejected: its operator hears of it on the tablet.</summary>
    public const string ReviewRejected = "review.rejected";

    /// <summary>A review waited past its due time: the policy's escalation role on the site.</summary>
    public const string ReviewEscalated = "review.escalated";

    /// <summary>A machine was held as unsafe: the site's managers and maintenance planners.</summary>
    public const string AssetUnsafe = "asset.unsafe";

    /// <summary>A continuity gap became a fraud suspicion (RULE-FO-11): executives and company administrators.</summary>
    public const string FraudSuspected = "integrity.fraud_suspected";

    /// <summary>A shift's end is missing (RULE-FO-08): the site's managers.</summary>
    public const string MissingEnd = "integrity.missing_end";

    public static IReadOnlyList<string> All { get; } = [ReviewRejected, ReviewEscalated, AssetUnsafe, FraudSuspected, MissingEnd];
}

/// <summary>
/// A notice from another module (ADR-0039 §1): the event it comes from (each person gets it once), its type, the values
/// its words name (never a finished sentence: each reader reads it in their own language, and only what their role may
/// see), where it opens, its site, and the person it is about when the audience is that person.
/// </summary>
public sealed record Notice(Guid SourceEventId, string Type, IReadOnlyDictionary<string, string> Parameters, string Link, Guid? SiteId)
{
    /// <summary>The person a notice is for when the rule says so (the operator of a rejected entry).</summary>
    public Guid? Person { get; init; }

    /// <summary>The role a notice goes to when the event names it (the escalation role of a review policy).</summary>
    public string? Role { get; init; }
}

/// <summary>
/// How a module hands over a notice (ADR-0039 §1), in the transaction of the outbox dispatch that carries its event,
/// so the notice is stored exactly once with the event marked as processed.
/// </summary>
public interface INotices
{
    Task PublishAsync(DbConnection connection, DbTransaction transaction, Notice notice, CancellationToken cancellationToken);
}
