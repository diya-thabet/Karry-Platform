using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Identity.Application;

public sealed record ChangeUserRoles(IReadOnlyList<RoleAssignment>? Roles);

/// <summary>
/// Keeps Keycloak's <c>mfa-required</c> in step with what a user's roles demand (US-P1-IAM-02). When a second factor
/// becomes required, the user's sessions end, so the next sign-in asks for it at once.
/// </summary>
public sealed class MfaSync(IIdentityProvider provider)
{
    /// <param name="user">The user whose roles changed (nothing to do without a Keycloak account).</param>
    /// <param name="wasRequired">What the roles demanded before; equal to <paramref name="required"/> on a retry.</param>
    /// <param name="required">What the roles demand now.</param>
    /// <param name="retry">Nothing changed in Karry (a repeated request): sessions end again when a factor is required, in case the first attempt stopped before.</param>
    /// <param name="cancellationToken">Cancels the Keycloak calls.</param>
    public async Task ApplyAsync(UserAccount user, bool wasRequired, bool required, bool retry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Subject is null)
        {
            return; // field users have no Keycloak account
        }

        await provider.SetMfaRequiredAsync(user.Subject, required, cancellationToken).ConfigureAwait(false);
        if (required && (!wasRequired || retry))
        {
            await provider.SignOutAsync(user.Subject, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Replaces a user's role assignments (US-P1-IAM-06 AC1, threat T-13). Same scope rules as deactivation (404 outside
/// the caller's sites, 403 when the user also works elsewhere); nobody changes their own roles; the new assignments
/// pass <see cref="UserAdministration.CheckGrant"/>; adding or removing a privileged role or a privileged permission
/// needs a recent sign-in. Karry first, then Keycloak (idempotent: repeating the request repairs a Keycloak failure).
/// </summary>
public sealed class ChangeUserRolesHandler(
    IIdentityStore store, RoleDirectory directory, UserAdministration administration, ICallerAccess caller, IAccessCache cache,
    MfaSync mfa, ITenantContext tenant, TimeProvider clock)
{
    /// <summary>
    /// With <paramref name="expectedVersion"/> (the form's <c>If-Match</c>), only the version of the user the
    /// administrator looked at is changed; otherwise <see cref="RecordVersions.Conflict"/> and nothing is saved (H-C1).
    /// </summary>
    public async Task<Result<UserAccount>> HandleAsync(Guid userId, ChangeUserRoles command, CancellationToken cancellationToken, uint? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = userId == Guid.Empty ? null : await store.FindUserAsync(new UserId(userId), cancellationToken).ConfigureAwait(false);
        if (user is null || !administration.CanSee(user))
        {
            return UserAdministration.NotFound;
        }

        if (!administration.CanManage(user))
        {
            return Error.Forbidden("identity.scope_exceeded", "This user also works outside the sites you manage.");
        }

        if (user.Subject is not null && user.Subject == administration.Subject)
        {
            return Error.BusinessRule("identity.cannot_change_own_roles", "You cannot change your own roles; ask another administrator.");
        }

        if (!RecordVersions.Matches(user.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (administration.CheckGrant(roles, command.Roles) is { } refused)
        {
            return refused;
        }

        var after = command.Roles?.Where(a => a is not null).ToList() ?? [];
        var touched = user.Roles.Except(after).Concat(after.Except(user.Roles));
        if (touched.Any(a => IsPrivileged(roles, a)) && StepUp.Check(caller, clock) is { } stepUp)
        {
            return stepUp;
        }

        var wasRequired = user.RequiresMfa(roles.Lookup);
        var changed = user.ChangeRoles(clock, command.Roles, roles.Lookup, administration.Subject);
        if (!changed.IsSuccess)
        {
            return changed.Error;
        }

        if (changed.Value)
        {
            var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return saved.Error;
            }

            cache.ForgetTenant(tenant.Current!.Value);
        }

        await mfa.ApplyAsync(user, wasRequired, user.RequiresMfa(roles.Lookup), retry: !changed.Value, cancellationToken).ConfigureAwait(false);
        return user;
    }

    /// <summary>A privileged role (second factor) or one containing a privileged permission; an unknown role counts as privileged.</summary>
    private bool IsPrivileged(TenantRoles roles, RoleAssignment assignment) =>
        roles.Find(assignment.Role) is not { } role
        || role.Privileged
        || role.Permissions.Any(p => directory.Catalog.Permission(p)?.Privileged == true);
}

/// <summary>
/// Sends a new invitation link to an invited user (expired or lost link); the previous link stops working. Same scope
/// rules as the other user changes.
/// </summary>
public sealed class ResendInvitationHandler(
    IIdentityStore store, IInvitationSender sender, ITenantDirectory tenants, ITenantContext tenant, IOptions<IdentityOptions> options,
    TimeProvider clock, UserAdministration administration)
{
    public async Task<Result<InvitedUser>> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = userId == Guid.Empty ? null : await store.FindUserAsync(new UserId(userId), cancellationToken).ConfigureAwait(false);
        if (user is null || !administration.CanSee(user))
        {
            return UserAdministration.NotFound;
        }

        if (!administration.CanManage(user))
        {
            return Error.Forbidden("identity.scope_exceeded", "This user also works outside the sites you manage.");
        }

        var previous = await store.FindOpenInvitationAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return Error.BusinessRule("identity.not_invited", "This user already accepted their invitation or was deactivated.");
        }

        var reissued = user.Reinvite(clock, previous, administration.Subject);
        if (!reissued.IsSuccess)
        {
            return reissued.Error;
        }

        var (invitation, token) = reissued.Value;
        store.Add(invitation);
        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        var company = await tenants.FindAsync(tenant.Current!.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {tenant.Current} is not in the catalog.");
        var delivered = await sender.SendAsync(InviteUserHandler.MessageFor(options.Value, company, user, invitation, token), cancellationToken)
            .ConfigureAwait(false);
        return new InvitedUser(user.Id.Value, invitation.Id.Value, invitation.ExpiresAt, delivered);
    }
}
