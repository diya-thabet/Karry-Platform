namespace Karry.Modules.Identity.Contracts;

/// <summary>
/// Who receives a notice (ADR-0039 §2), resolved at delivery time so a person who left a site no longer hears of it:
/// the active users holding one of some roles on a site (or across the company), and the user behind a signed-in
/// account.
/// </summary>
public interface INoticeAudiences
{
    /// <summary>
    /// Active users holding one of <paramref name="roles"/> on <paramref name="siteId"/> (an assignment to that site or
    /// to every site); with no site, holders of the roles across the company only.
    /// </summary>
    Task<IReadOnlyList<Guid>> HoldersAsync(IReadOnlyCollection<string> roles, Guid? siteId, CancellationToken cancellationToken);

    /// <summary>The user of a signed-in account (the token's subject), or null.</summary>
    Task<Guid?> UserIdOfAsync(string subject, CancellationToken cancellationToken);
}
