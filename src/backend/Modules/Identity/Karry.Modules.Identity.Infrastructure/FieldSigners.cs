using Karry.Modules.Identity.Contracts;
using Karry.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>The tablet's unlock rule (<see cref="IdentitySync"/>) applied to one signer; row-level security keeps it in the company.</summary>
internal sealed class FieldSigners(IdentityDbContext db) : IFieldSigners
{
    public async Task<FieldSignerRefusal?> RefusalAsync(Guid userId, Guid siteId, CancellationToken cancellationToken)
    {
        var id = new UserId(userId);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
        return user switch
        {
            null => new FieldSignerRefusal(FieldSignerRefusals.Unknown),
            { Kind: not UserKind.Field } or { EmployeeId: null } => new FieldSignerRefusal(FieldSignerRefusals.NotFieldUser),
            { Status: UserStatus.Deactivated } => new FieldSignerRefusal(FieldSignerRefusals.Deactivated, user.DeactivatedAt),
            { Status: not UserStatus.Active } => new FieldSignerRefusal(FieldSignerRefusals.Inactive),
            _ when !user.Roles.Any(r => r.SiteId is null || r.SiteId == siteId) => new FieldSignerRefusal(FieldSignerRefusals.NotOnSite),
            _ => null,
        };
    }
}
