using Karry.BuildingBlocks.Entitlements;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Entitlements;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>
/// The office-seat meter (US-P1-IAM-03 AC4): office users of the tenant who are invited or active. An open invitation
/// holds a seat; a deactivated user frees it. Field users are never counted.
/// </summary>
internal sealed class OfficeSeats(IdentityDbContext db) : IUsageSource
{
    public string Metric => Metrics.OfficeUsers;

    public async Task<long> CountAsync(CancellationToken cancellationToken) =>
        await db.Users.LongCountAsync(u => u.Kind == UserKind.Office && u.Status != UserStatus.Deactivated, cancellationToken).ConfigureAwait(false);
}
