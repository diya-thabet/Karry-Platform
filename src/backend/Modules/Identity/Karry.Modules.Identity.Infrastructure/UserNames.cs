using Karry.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>Row-level security keeps the lookup inside the current company.</summary>
internal sealed class UserNames(IdentityDbContext db) : IUserNames
{
    public async Task<IReadOnlyDictionary<string, string>> BySubjectAsync(IReadOnlyCollection<string> subjects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var people = await db.Users.AsNoTracking()
            .Where(u => u.Subject != null && subjects.Contains(u.Subject))
            .Select(u => new { Subject = u.Subject!, u.FirstName, u.LastName })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return people.ToDictionary(p => p.Subject, p => $"{p.FirstName} {p.LastName}", StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ByIdAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var wanted = userIds.Select(id => new Domain.UserId(id)).ToList();
        var people = await db.Users.AsNoTracking()
            .Where(u => wanted.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return people.ToDictionary(p => p.Id.Value, p => $"{p.FirstName} {p.LastName}");
    }
}
