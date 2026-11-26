using Karry.Modules.Identity.Contracts;
using Karry.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>Audiences of notices (ADR-0039 §2): active users and their role assignments; row-level security keeps them in the company.</summary>
internal sealed class NoticeAudiences(IdentityDbContext db) : INoticeAudiences
{
    public async Task<IReadOnlyList<Guid>> HoldersAsync(IReadOnlyCollection<string> roles, Guid? siteId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);
        // Role assignments are a JSON column: a company has tens to hundreds of people, read once per notice.
        var active = await db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. active
            .Where(u => u.Roles.Any(r => roles.Contains(r.Role, StringComparer.Ordinal) && (r.SiteId is null || (siteId is { } site && r.SiteId == site))))
            .Select(u => u.Id.Value)];
    }

    public async Task<NoticeContact?> ContactOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        var id = new UserId(userId);
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == id && u.Status == UserStatus.Active && u.Email != null)
            .Select(u => new { u.Email, u.FirstName, u.LastName })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return user is null ? null : new NoticeContact(user.Email!, $"{user.FirstName} {user.LastName}".Trim());
    }

    public async Task<Guid?> UserIdOfAsync(string subject, CancellationToken cancellationToken) =>
        (await db.Users.AsNoTracking().Where(u => u.Subject == subject).Select(u => (UserId?)u.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false))?.Value;
}
