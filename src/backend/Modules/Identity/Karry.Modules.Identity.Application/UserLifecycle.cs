using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Application;

public sealed record AcceptInvitation(string? Token, string? Password);

/// <summary>
/// Accepts an invitation (anonymous: the token is the credential). The tenant comes from the token itself, so the
/// lookup runs under that tenant's row-level security; an unknown, foreign or malformed token gets the same answer.
/// The password goes straight to Keycloak (its policy decides) and is never stored or logged by Karry.
/// </summary>
public sealed class AcceptInvitationHandler(IIdentityStore store, IIdentityProvider provider, ITenantContext tenant, TimeProvider clock)
{
    private static readonly Error Unknown = Error.NotFound("identity.invitation_invalid", "This invitation link is not valid.");

    public async Task<Result<Guid>> HandleAsync(AcceptInvitation command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Invitation.TenantOf(command.Token) is not { } owner || string.IsNullOrEmpty(command.Password))
        {
            return Unknown;
        }

        tenant.Set(owner);
        var invitation = await store.FindInvitationAsync(Invitation.HashToken(command.Token!), cancellationToken).ConfigureAwait(false);
        var user = invitation is null ? null : await store.FindUserAsync(invitation.UserId, cancellationToken).ConfigureAwait(false);
        if (invitation is null || invitation.RevokedAt is not null || user?.Subject is null)
        {
            return Unknown;
        }

        // Refuse used or expired links before touching Keycloak; Accept re-checks atomically with the save.
        if (invitation.AcceptedAt is not null || user.Status != UserStatus.Invited)
        {
            return Error.Conflict("identity.invitation_used", "This invitation was already used.");
        }

        if (clock.GetUtcNow() >= invitation.ExpiresAt)
        {
            return Error.BusinessRule("identity.invitation_expired", "This invitation has expired. Ask your administrator for a new one.");
        }

        var activated = await provider.ActivateAsync(user.Subject, command.Password, cancellationToken).ConfigureAwait(false);
        if (!activated.IsSuccess)
        {
            return activated.Error;
        }

        var accepted = user.Accept(invitation, clock);
        if (!accepted.IsSuccess)
        {
            return accepted.Error;
        }

        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? user.Id.Value : saved.Error;
    }
}

public sealed record CreateFieldUser(string? FirstName, string? LastName, string? EmployeeId, string? Pin, IReadOnlyList<RoleAssignment>? Roles);

/// <summary>A field user with employee ID and PIN (US-P1-IAM-03 AC2); enrolled on a tablet by QR in P1.15.</summary>
public sealed class CreateFieldUserHandler(
    IIdentityStore store, ITenantContext tenant, TimeProvider clock, UserAdministration administration, RoleDirectory directory)
{
    public async Task<Result<Guid>> HandleAsync(CreateFieldUser command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (administration.CheckGrant(roles, command.Roles) is { } refused)
        {
            return refused;
        }

        var current = tenant.Current ?? throw new InvalidOperationException("Creating a user needs a tenant.");
        var user = UserAccount.CreateFieldUser(
            clock, current, command.FirstName, command.LastName, command.EmployeeId, command.Pin, command.Roles, administration.Subject, roles.Lookup);
        if (!user.IsSuccess)
        {
            return user.Error;
        }

        var added = await store.AddAsync(user.Value, null, cancellationToken).ConfigureAwait(false);
        return added.IsSuccess ? user.Value.Id.Value : added.Error;
    }
}

/// <summary>
/// Deactivates a leaver (US-P1-IAM-03 AC3, TC-P1-IAM-007): Karry first (the API refuses the user's tokens within the
/// revocation cache time), then Keycloak (account disabled, sessions ended). Idempotent: repeating it re-applies the
/// Keycloak part, so a failure there is fixed by retrying. An administrator cannot deactivate themselves. A user
/// outside the caller's scope is "not found" (US-P1-IAM-06 AC2): its existence is not revealed.
/// </summary>
public sealed class DeactivateUserHandler(
    IIdentityStore store, IIdentityProvider provider, IRevocationCache revocations, ITenantContext tenant, TimeProvider clock, UserAdministration administration)
{
    public async Task<Result<bool>> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var callerSubject = administration.Subject;
        var user = userId == Guid.Empty ? null : await store.FindUserAsync(new UserId(userId), cancellationToken).ConfigureAwait(false);
        if (user is null || !administration.CanSee(user))
        {
            return UserAdministration.NotFound;
        }

        if (!administration.CanManage(user))
        {
            return Error.Forbidden("identity.scope_exceeded", "This user also works outside the sites you manage.");
        }

        if (user.Subject is not null && user.Subject == callerSubject)
        {
            return Error.BusinessRule("identity.cannot_deactivate_self", "You cannot deactivate your own account.");
        }

        if (user.Deactivate(clock, callerSubject))
        {
            var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return saved.Error;
            }
        }

        if (user.Subject is not null)
        {
            revocations.Forget(tenant.Current!.Value, user.Subject);
            await provider.DisableAndSignOutAsync(user.Subject, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}

/// <summary>First Tenant Admin of a new tenant (US-P1-TEN-01 AC1), called by the Tenancy module after provisioning.</summary>
public sealed class FirstAdminInvitation(IIdentityStore store, InviteUserHandler invite, ITenantContext context) : IFirstAdminInvitation
{
    public async Task<Result<FirstAdminInvitationResult>> InviteAsync(
        TenantId tenant, string firstName, string lastName, string email, string? actor, CancellationToken cancellationToken)
    {
        context.Set(tenant);
        if (await store.HasAdministratorAsync(cancellationToken).ConfigureAwait(false))
        {
            return new FirstAdminInvitationResult(Invited: false, Delivered: false);
        }

        var invited = await invite.InviteAsync(new InviteUser(firstName, lastName, email, null, [new RoleAssignment(Roles.TenantAdmin, null)]), actor, null, cancellationToken)
            .ConfigureAwait(false);
        return invited.IsSuccess ? new FirstAdminInvitationResult(Invited: true, invited.Value.Delivered) : invited.Error;
    }
}
