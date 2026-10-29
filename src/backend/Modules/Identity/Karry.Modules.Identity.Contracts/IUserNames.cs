namespace Karry.Modules.Identity.Contracts;

/// <summary>
/// The names of the current company's people by their sign-in subject (<c>sub</c>), for screens that record who did
/// something by subject, such as the audit log (US-P1-AUD-04). Subjects of no person of the company are left out.
/// </summary>
public interface IUserNames
{
    Task<IReadOnlyDictionary<string, string>> BySubjectAsync(IReadOnlyCollection<string> subjects, CancellationToken cancellationToken);
}
