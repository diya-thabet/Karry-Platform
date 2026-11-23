namespace Karry.Modules.Identity.Contracts;

/// <summary>
/// Whether a person may sign on a tablet of a site (threat R-13, RULE-FO-09): the tablet unlocks only for the company's
/// active field users with an employee ID and a role on the site or across the company (the same rule as the users it
/// pulls). A signer outside it came from a tampered app, or was deactivated or moved before the fact arrived.
/// </summary>
public interface IFieldSigners
{
    /// <summary>Null when the person may sign on the site now; otherwise why not (<see cref="FieldSignerRefusals"/>).</summary>
    Task<FieldSignerRefusal?> RefusalAsync(Guid userId, Guid siteId, CancellationToken cancellationToken);
}

/// <summary>Why a signer may not sign on a site; <c>DeactivatedAt</c> when the person was deactivated.</summary>
public sealed record FieldSignerRefusal(string Reason, DateTimeOffset? DeactivatedAt = null);

/// <summary>The reasons of <see cref="FieldSignerRefusal"/>.</summary>
public static class FieldSignerRefusals
{
    /// <summary>No person of the company has this ID.</summary>
    public const string Unknown = "unknown";

    /// <summary>A person of the company who does not sign on tablets (an office account, or no employee ID).</summary>
    public const string NotFieldUser = "not_field_user";

    /// <summary>Invited, never active.</summary>
    public const string Inactive = "inactive";

    public const string Deactivated = "deactivated";

    /// <summary>Active, but with no role on this site nor across the company.</summary>
    public const string NotOnSite = "not_on_site";
}
