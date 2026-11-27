using System.Data.Common;

namespace Karry.Modules.Integrity.Contracts;

/// <summary>What a flag is about, or is linked to: an entity of the owning module (<c>shift_entry</c>, <c>meter</c>, <c>device</c> …).</summary>
public sealed record FlagSubject(string Type, Guid Id);

/// <summary>
/// One thing a rule found about a subject (ADR-0036 §3). <see cref="Key"/> tells findings of the same rule and subject
/// apart (for continuity: the two segments of a gap); <see cref="Related"/> are the other entities involved;
/// <see cref="Evidence"/> are links to what shows it (photos as <c>sha256:</c> references); <see cref="Details"/> are the
/// values the explanation names (the two meter values, the skew in minutes …).
/// </summary>
public sealed record Finding(string Key, IReadOnlyList<FlagSubject> Related, IReadOnlyList<string> Evidence, IReadOnlyDictionary<string, string> Details)
{
    /// <summary>The site the finding belongs to, when it is not the subject's (a meter that moved between its two segments).</summary>
    public Guid? SiteId { get; init; }
}

/// <summary>A rule's settings in the current company: switched on, and its thresholds by name (ADR-0036 §1).</summary>
public sealed record RuleSettings(string Rule, bool Enabled, IReadOnlyDictionary<string, decimal> Thresholds);

/// <summary>
/// How modules raise and close integrity flags (ADR-0036 §3), in their own transaction so a fact and its flags commit
/// together. <c>SyncAsync</c> gives every finding that holds now for a subject and rule: missing flags are opened, open
/// flags no longer found are closed with the reason <c>re-evaluated</c>; a key a person settled is never raised again
/// (ADR-0037 §7). A switched-off rule opens nothing.
/// </summary>
public interface IIntegrityFlags
{
    Task<RuleSettings> SettingsAsync(string rule, CancellationToken cancellationToken);

    Task SyncAsync(DbConnection connection, DbTransaction transaction, FlagSubject subject, string rule, Guid? siteId, IReadOnlyList<Finding> findings, CancellationToken cancellationToken);
}

/// <summary>
/// Who took part in what a flag is linked to (ADR-0037 §7): the owning module answers whether a person produced one of
/// the related entities (for a continuity gap: signed one of its two segments). A person never settles
/// a gap they took part in.
/// </summary>
public interface IFlagParticipants
{
    Task<bool> TookPartAsync(Guid person, IReadOnlyList<FlagSubject> related, CancellationToken cancellationToken);
}

/// <summary>The rule codes of the detection catalogue that P2.7 implements (detection-rules.md).</summary>
public static class IntegrityRules
{
    public const string Continuity = "RULE-FO-01";
    public const string Delta = "RULE-FO-02";
    public const string ClockSkew = "RULE-FO-03";
    public const string Geofence = "RULE-FO-04";
    public const string PhotoWithoutCapture = "RULE-FO-05";
    public const string PhotoReused = "RULE-FO-06";
    public const string RevokedDevice = "RULE-FO-07";

    /// <summary>A segment still open some time after its shift ended (US-P2-VER-08 AC2, ADR-0038 §5).</summary>
    public const string MissingEnd = "RULE-FO-08";

    /// <summary>A truck created at the desk for an unknown plate, open until someone verifies it (US-P3-SAL-04 AC2, P3.2).</summary>
    public const string UnverifiedTruck = "RULE-WB-09";

    /// <summary>A fact signed by someone who may not sign on the site's tablets (threat R-13).</summary>
    public const string UnknownSigner = "RULE-FO-09";

    /// <summary>A start without a recent pre-start checklist where the machine type requires one (threat R-14).</summary>
    public const string MissingChecklist = "RULE-FO-10";

    /// <summary>A continuity gap settled as unexplained, or left unsettled beyond 48 h (US-P2-FO-08 AC2, ADR-0037 §7).</summary>
    public const string FraudSuspected = "RULE-FO-11";
}
