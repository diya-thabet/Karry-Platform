using Karry.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>The field account linked to a reviewer's office account (ADR-0037 §4); row-level security keeps it in the company.</summary>
internal sealed class Reviewers(IdentityDbContext db) : IReviewers
{
    public Task<Guid?> FieldAccountOfAsync(string subject, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().Where(u => u.Subject == subject).Select(u => u.LinkedFieldUserId).SingleOrDefaultAsync(cancellationToken);
}
