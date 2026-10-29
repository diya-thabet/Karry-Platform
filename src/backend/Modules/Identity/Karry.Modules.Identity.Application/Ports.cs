using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Application;

/// <summary>A Keycloak account to create for an invited office user (disabled until the invitation is accepted).</summary>
public sealed record NewAccount(TenantId Tenant, string Username, string? Email, string FirstName, string LastName, bool MfaRequired);

/// <summary>
/// The identity provider (Keycloak admin API, <c>deploy/keycloak</c>). Every call is idempotent: invitation, acceptance
/// and deactivation retry them safely after a partial failure.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>
    /// Creates the account, disabled, with the admin-only <c>tenant_id</c> attribute; returns its subject. An existing
    /// disabled account of the same tenant with the same username (left by a failed earlier attempt) is adopted.
    /// Conflict <c>identity.account_exists</c> when the username belongs to someone else.
    /// </summary>
    Task<Result<string>> CreateAccountAsync(NewAccount account, CancellationToken cancellationToken);

    /// <summary>Sets the user's password and enables the account. Validation <c>identity.password_rejected</c> on the realm's password policy.</summary>
    Task<Result<string>> ActivateAsync(string subject, string password, CancellationToken cancellationToken);

    /// <summary>Disables the account and ends all its sessions (refresh tokens die at once).</summary>
    Task DisableAndSignOutAsync(string subject, CancellationToken cancellationToken);

    /// <summary>Gives or takes the realm role <c>mfa-required</c> (second factor at sign-in); idempotent.</summary>
    Task SetMfaRequiredAsync(string subject, bool required, CancellationToken cancellationToken);

    /// <summary>Ends all sessions of the account (it signs in again, with a second factor if now required).</summary>
    Task SignOutAsync(string subject, CancellationToken cancellationToken);

    /// <summary>The tenant's organization (alias = slug) and its app addresses as allowed redirect URIs.</summary>
    Task EnsureTenantAsync(TenantId tenant, string slug, string name, CancellationToken cancellationToken);
}

/// <summary>
/// Karry operator accounts for local and demo environments only (<c>seed-demo</c>, refused in production): attribute
/// <c>platform_role=operator</c>, no tenant, realm role <c>mfa-required</c> (ADR-0029 §5). Kept out of
/// <see cref="IIdentityProvider"/> so that no request path can create an operator.
/// </summary>
public interface IDemoOperatorAccounts
{
    /// <summary>Creates the account enabled with this password, or leaves an existing one as it is; returns its subject.</summary>
    Task<string> EnsureOperatorAsync(string email, string firstName, string lastName, string password, CancellationToken cancellationToken);
}

/// <summary>An invitation to deliver: the link is the only copy of the token (only its hash is stored).</summary>
public sealed record InvitationMessage(InvitationChannel Channel, string Destination, string RecipientName, string CompanyName, Uri Link, DateTimeOffset ExpiresAt);

/// <summary>Delivers invitations by e-mail or phone (SMS/WhatsApp port, stubbed until the owner picks a provider).</summary>
public interface IInvitationSender
{
    /// <summary>False when the channel is unavailable or delivery failed (the invitation stays valid; it can be re-sent).</summary>
    Task<bool> SendAsync(InvitationMessage message, CancellationToken cancellationToken);
}

/// <summary>Persistence of users and invitations (current tenant only: row-level security and query filters).</summary>
public interface IIdentityStore
{
    Task<UserAccount?> FindUserAsync(UserId id, CancellationToken cancellationToken);

    Task<UserAccount?> FindBySubjectAsync(string subject, CancellationToken cancellationToken);

    Task<Invitation?> FindInvitationAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>The user's latest invitation that is neither accepted nor replaced.</summary>
    Task<Invitation?> FindOpenInvitationAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>Tracks a new invitation of a loaded user (saved by <see cref="SaveAsync"/>).</summary>
    void Add(Invitation invitation);

    Task<bool> HasAdministratorAsync(CancellationToken cancellationToken);

    /// <summary>A user with this e-mail (lower-case) or employee ID exists, whatever its status.</summary>
    Task<bool> ExistsAsync(string? email, string? employeeId, CancellationToken cancellationToken);

    /// <summary>Adds a new user (and its invitation) with their events; Conflict on a duplicate e-mail, phone, employee ID or account.</summary>
    Task<Result<UserAccount>> AddAsync(UserAccount user, Invitation? invitation, CancellationToken cancellationToken);

    /// <summary>Records an imported sign-in failure with its ledger event; false when it was imported before.</summary>
    Task<bool> AddSignInFailureAsync(SignInFailure failure, CancellationToken cancellationToken);

    /// <summary>Saves changes to loaded aggregates; Conflict <c>identity.concurrent_change</c> when another request changed them first.</summary>
    Task<Result<bool>> SaveAsync(CancellationToken cancellationToken);

    /// <summary>The users with any of these e-mails (lower-case), phones or employee IDs (tracked, for an import), whatever their status.</summary>
    Task<IReadOnlyList<UserAccount>> FindForImportAsync(
        IReadOnlyCollection<string> emails, IReadOnlyCollection<string> phones, IReadOnlyCollection<string> employeeIds, CancellationToken cancellationToken);

    /// <summary>Tracks a new user (and its invitation), saved with the next save of the unit of work.</summary>
    void Track(UserAccount user, Invitation? invitation);

    /// <summary>
    /// Saves everything tracked in one transaction (an import); false, with nothing saved, when an e-mail, phone or
    /// employee ID was taken or a user changed meanwhile.
    /// </summary>
    Task<bool> TrySaveAllAsync(CancellationToken cancellationToken);
}

/// <summary>Forgets a cached "is this account revoked" answer at once (the deactivating instance needs no 30 s wait).</summary>
public interface IRevocationCache
{
    void Forget(TenantId tenant, string subject);
}

/// <summary>Persistence of the tenant's custom roles and default-role field levels (current tenant only).</summary>
public interface IRoleStore
{
    Task<IReadOnlyList<CustomRole>> CustomRolesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DefaultRoleSettings>> DefaultRoleSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Some user who is not deactivated holds this role (at any scope).</summary>
    Task<bool> IsHeldAsync(string role, CancellationToken cancellationToken);

    /// <summary>Office users with a Keycloak account, not deactivated, who hold this role (second-factor sync).</summary>
    Task<IReadOnlyList<UserAccount>> AccountHoldersAsync(string role, CancellationToken cancellationToken);

    void Add(CustomRole role);

    void Add(DefaultRoleSettings settings);

    void Remove(CustomRole role);

    /// <summary>Saves with the ledger events; Conflict <c>identity.role_exists</c> on a duplicate name, <c>identity.concurrent_change</c> on a race.</summary>
    Task<Result<bool>> SaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Forgets the cached access of a tenant's users at once on this instance after a role change (other instances follow
/// within the 30 s cache lifetime).
/// </summary>
public interface IAccessCache
{
    void ForgetTenant(TenantId tenant);
}
