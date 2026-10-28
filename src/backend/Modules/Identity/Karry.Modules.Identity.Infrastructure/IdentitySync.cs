using Karry.BuildingBlocks.Sync;
using Karry.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Karry.Modules.Identity.Infrastructure;

/// <summary>
/// A field user as the field app keeps it (<c>@karry/offline</c> <c>FieldUserRow</c>): <see cref="Pin"/> lets the tablet
/// check the PIN offline without the hash (ADR-0028 §4), null until a PIN is set.
/// </summary>
public sealed record FieldUserSyncRow(Guid Id, string EmployeeId, string Name, PinVerifier? Pin);

/// <summary>
/// The active field users who work on the tablet's site or company-wide (ADR-0028 §4–§5), with PIN verifiers keyed by
/// this tablet's own PIN key. A user deactivated, moved off the site or turned office is sent as a remove, so they can
/// no longer unlock after the next pull (TC-P1-IAM-004).
/// </summary>
internal sealed class IdentitySync(IdentityDbContext db) : ISyncSource
{
    public async Task<IReadOnlyList<SyncDelta>> ChangesAsync(SyncScope scope, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var deviceId = new DeviceId(scope.DeviceId);
        var pinKey = await db.Devices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => d.PinKey).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (pinKey is null)
        {
            return [new SyncDelta("users", [], [])];
        }

        // Roles are JSON: the site filter runs here, on the field users (snapshot) or the users changed (delta).
        var users = await db.Users.AsNoTracking()
            .Where(u => u.Kind == UserKind.Field && (since == null ? u.Status == UserStatus.Active : u.ChangedAt >= since))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool InScope(UserAccount u) =>
            u.Kind == UserKind.Field && u.Status == UserStatus.Active && u.EmployeeId is not null
            && u.Roles.Any(r => r.SiteId is null || r.SiteId == scope.SiteId);

        return
        [
            new SyncDelta(
                "users",
                [.. users.Where(InScope).OrderBy(u => u.EmployeeId, StringComparer.Ordinal)
                    .Select(u => new FieldUserSyncRow(u.Id.Value, u.EmployeeId!, u.DisplayName, u.PinHash is null ? null : Pin.Verifier(u.PinHash, pinKey)))],
                [.. users.Where(u => !InScope(u)).Select(u => u.Id.Value)]),
        ];
    }
}
