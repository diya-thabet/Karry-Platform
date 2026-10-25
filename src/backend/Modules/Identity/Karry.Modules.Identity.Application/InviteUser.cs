using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Identity.Application;

/// <summary>Settings of the Identity module (section <c>Identity</c>).</summary>
public sealed class IdentityOptions
{
    public const string Section = "Identity";

    /// <summary>
    /// Address of a tenant's portal, <c>{slug}</c> replaced by the tenant slug: <c>https://{slug}.karry.app</c> in
    /// production, <c>http://{slug}.localhost:5173</c> locally. Invitation links point there.
    /// </summary>
    public string PortalOrigin { get; set; } = "http://{slug}.localhost:5173";

    /// <summary>Address of a tenant's field app (same placeholder); registered with Keycloak next to the portal.</summary>
    public string FieldOrigin { get; set; } = "http://{slug}.localhost:5174";

    public Uri PortalFor(string slug) => new(PortalOrigin.Replace("{slug}", slug, StringComparison.Ordinal));

    public Uri FieldFor(string slug) => new(FieldOrigin.Replace("{slug}", slug, StringComparison.Ordinal));
}

public sealed record InviteUser(string? FirstName, string? LastName, string? Email, string? Phone, IReadOnlyList<RoleAssignment>? Roles);

public sealed record InvitedUser(Guid UserId, Guid InvitationId, DateTimeOffset ExpiresAt, bool Delivered);

/// <summary>
/// Invites an office user (US-P1-IAM-03 AC1): the Karry user and invitation, a disabled Keycloak account carrying the
/// tenant (and <c>mfa-required</c> for privileged roles), then the link by e-mail or phone. The caller holds
/// <c>identity.users.manage</c> (endpoint) and may give these roles (<see cref="UserAdministration.CheckGrant"/>; default or custom roles).
/// The token exists only in the delivered link.
/// </summary>
public sealed class InviteUserHandler(
    IIdentityStore store, IIdentityProvider provider, IInvitationSender sender, ITenantDirectory tenants,
    ITenantContext tenant, IOptions<IdentityOptions> options, TimeProvider clock, UserAdministration administration, RoleDirectory directory)
{
    public async Task<Result<InvitedUser>> HandleAsync(InviteUser command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        return administration.CheckGrant(roles, command.Roles) is { } refused
            ? refused
            : await InviteAsync(command, administration.Subject, roles, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Without the caller check: the first administrator of a tenant, invited by the platform.</summary>
    internal async Task<Result<InvitedUser>> InviteAsync(InviteUser command, string? actor, TenantRoles? roles, CancellationToken cancellationToken)
    {
        var current = tenant.Current ?? throw new InvalidOperationException("Inviting needs a tenant.");
        roles ??= await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        var invited = UserAccount.Invite(clock, current, command.FirstName, command.LastName, command.Email, command.Phone, command.Roles, actor, roles.Lookup);
        if (!invited.IsSuccess)
        {
            return invited.Error;
        }

        var (user, invitation, token) = invited.Value;
        var company = await tenants.FindAsync(current, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {current} is not in the catalog.");

        // Phone users sign in with their number (digits only: Keycloak usernames do not allow '+').
        var username = user.Email ?? user.Phone!.TrimStart('+');
        var account = await provider.CreateAccountAsync(
            new NewAccount(current, username, user.Email, user.FirstName, user.LastName, user.RequiresMfa(roles.Lookup)), cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        user.LinkAccount(account.Value);
        var added = await store.AddAsync(user, invitation, cancellationToken).ConfigureAwait(false);
        if (!added.IsSuccess)
        {
            return added.Error;
        }

        var link = new Uri(options.Value.PortalFor(company.Slug), $"/invitation#token={Uri.EscapeDataString(token)}");
        var delivered = await sender.SendAsync(
            new InvitationMessage(invitation.Channel, user.Email ?? user.Phone!, user.DisplayName, company.Name, link, invitation.ExpiresAt),
            cancellationToken).ConfigureAwait(false);
        return new InvitedUser(user.Id.Value, invitation.Id.Value, invitation.ExpiresAt, delivered);
    }
}
