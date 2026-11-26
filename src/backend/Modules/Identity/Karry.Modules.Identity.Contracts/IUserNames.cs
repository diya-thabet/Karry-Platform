namespace Karry.Modules.Identity.Contracts;

/// <summary>
/// The names of the current company's people by their sign-in subject (<c>sub</c>), for screens that record who did
/// something by subject, such as the audit log (US-P1-AUD-04), or by their user ID, for documents that name the people
/// of their records (the shift report, ADR-0040). Subjects or IDs of no person of the company are left out.
/// </summary>
public interface IUserNames
{
    Task<IReadOnlyDictionary<string, string>> BySubjectAsync(IReadOnlyCollection<string> subjects, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> ByIdAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
