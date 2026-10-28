using System.Text.RegularExpressions;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Identity.Domain;

public readonly record struct UserId(Guid Value) : IEntityId<UserId>
{
    public static UserId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

public enum UserKind
{
    /// <summary>Signs in with Keycloak (portal, field app with an account): e-mail or phone invitation.</summary>
    Office = 1,

    /// <summary>Employee ID + PIN on a shared tablet, no e-mail and no Keycloak account (US-P1-IAM-03 AC2).</summary>
    Field = 2,
}

public enum UserStatus
{
    Invited = 1,
    Active = 2,

    /// <summary>Final: sessions revoked, API access refused within a minute; the record (and the name) stays (AC3).</summary>
    Deactivated = 3,
}

// Ledger events carry identifiers only, never names, e-mails or phone numbers: the ledger is immutable, personal data
// must stay erasable (it lives in the users table).
[EventType("identity.user_invited.v1")]
[AuditSubject("user", nameof(UserId))]
public sealed record UserInvited(Guid UserId, Guid InvitationId, string Channel, IReadOnlyList<RoleAssignment> Roles, DateTimeOffset ExpiresAt, string? Actor) : DomainEvent;

[EventType("identity.invitation_accepted.v1")]
[AuditSubject("user", nameof(UserId))]
public sealed record InvitationAccepted(Guid UserId, Guid InvitationId) : DomainEvent;

[EventType("identity.field_user_created.v1")]
[AuditSubject("user", nameof(UserId))]
public sealed record FieldUserCreated(Guid UserId, IReadOnlyList<RoleAssignment> Roles, string? Actor) : DomainEvent;

[EventType("identity.user_deactivated.v1")]
[AuditSubject("user", nameof(UserId))]
public sealed record UserDeactivated(Guid UserId, string? Actor) : DomainEvent;

/// <summary>A user's role assignments changed (US-P1-IAM-06 AC1); before and after for the ledger (T-13).</summary>
[EventType("identity.user_roles_changed.v1")]
[AuditSubject("user", nameof(UserId))]
public sealed record UserRolesChanged(Guid UserId, IReadOnlyList<RoleAssignment> Before, IReadOnlyList<RoleAssignment> After, string? Actor) : DomainEvent;

/// <summary>A new invitation link replaced the previous one, which no longer works.</summary>
/// <summary>A field user's PIN was set by an administrator (the PIN itself is never in the ledger).</summary>
[EventType("identity.user_pin_set.v1")]
[AuditSubject("user", nameof(UserId))]
public sealed record UserPinSet(Guid UserId, string? Actor) : DomainEvent;

/// <summary>A field user typed a wrong PIN too often on a tablet's online unlock: locked for a while (ADR-0028 §4).</summary>
[EventType("identity.user_pin_locked.v1")]
[AuditSubject("user", nameof(UserId))]
public sealed record UserPinLocked(Guid UserId, Guid DeviceId, DateTimeOffset LockedUntil) : DomainEvent;

[EventType("identity.invitation_reissued.v1")]
[AuditSubject("user", nameof(UserId))]
public sealed record InvitationReissued(Guid UserId, Guid InvitationId, Guid ReplacedInvitationId, DateTimeOffset ExpiresAt, string? Actor) : DomainEvent;

/// <summary>
/// A person of a tenant (US-P1-IAM-03). Office users are linked to a Keycloak account by its subject (the token's
/// <c>sub</c>); field users are identified by employee ID and PIN. Never deleted: deactivation keeps the name for
/// historical records.
/// </summary>
public sealed partial class UserAccount : AggregateRoot<UserId>, ITenantOwned
{
    public const int NameMaxLength = 60;
    public const int EmployeeIdMaxLength = 32;

    private UserAccount(UserId id, TenantId tenantId, UserKind kind, string firstName, string lastName, IReadOnlyList<RoleAssignment> roles, DateTimeOffset createdAt)
        : base(id)
    {
        TenantId = tenantId;
        Kind = kind;
        FirstName = firstName;
        LastName = lastName;
        Roles = roles;
        CreatedAt = createdAt;
        ChangedAt = createdAt;
    }

    public TenantId TenantId { get; private set; }

    public UserKind Kind { get; private set; }

    public UserStatus Status { get; private set; }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public string DisplayName => $"{FirstName} {LastName}";

    /// <summary>Lower-cased; office users invited by e-mail.</summary>
    public string? Email { get; private set; }

    /// <summary>E.164 (<c>+21612345678</c>); office users invited by phone.</summary>
    public string? Phone { get; private set; }

    public string? EmployeeId { get; private set; }

    /// <summary>Keycloak user ID (<c>sub</c> claim) of an office user.</summary>
    public string? Subject { get; private set; }

    public string? PinHash { get; private set; }

    /// <summary>Wrong PINs in a row at an online unlock; reset by a right PIN, a new PIN or a lock.</summary>
    public int PinFailures { get; private set; }

    public DateTimeOffset? PinLockedUntil { get; private set; }

    /// <summary>Wrong PINs in a row that lock the user's online unlock, and for how long (a 6-digit PIN is guessable).</summary>
    public const int PinFailuresBeforeLock = 5;

    public static readonly TimeSpan PinLockDuration = TimeSpan.FromMinutes(15);

    public IReadOnlyList<RoleAssignment> Roles { get; private set; }

    /// <summary>Some role demands a second factor (Keycloak role <c>mfa-required</c>); unknown roles count as privileged.</summary>
    public bool RequiresMfa(RoleLookup roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return Roles.Any(r => roles(r.Role)?.Privileged ?? true);
    }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Last change a tablet must learn about (status, roles, PIN): the field reference data is pulled by it (ADR-0028 §5).</summary>
    public DateTimeOffset ChangedAt { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    public bool HasRole(string role) => Roles.Any(r => r.Role == role);

    /// <summary>An office user invited by e-mail or phone; becomes active when the invitation is accepted.</summary>
    public static Result<(UserAccount User, Invitation Invitation, string Token)> Invite(
        TimeProvider clock, TenantId tenant, string? firstName, string? lastName, string? email, string? phone,
        IReadOnlyList<RoleAssignment>? roles, string? actor, RoleLookup? known = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var common = Validate(firstName, lastName, roles, known ?? Domain.Roles.Find);
        if (!common.IsSuccess)
        {
            return common.Error;
        }

        var contact = Contact(email, phone);
        if (!contact.IsSuccess)
        {
            return contact.Error;
        }

        var (first, last, assignments) = common.Value;
        var user = new UserAccount(EntityId.New<UserId>(clock), tenant, UserKind.Office, first, last, assignments, clock.GetUtcNow())
        {
            Status = UserStatus.Invited,
            Email = contact.Value.Email,
            Phone = contact.Value.Phone,
        };
        var channel = user.Email is not null ? InvitationChannel.Email : InvitationChannel.Phone;
        var (invitation, token) = Invitation.Issue(clock, tenant, user.Id, channel);
        user.Raise(new UserInvited(user.Id.Value, invitation.Id.Value, channel.ToString().ToLowerInvariant(), assignments, invitation.ExpiresAt, actor)
        {
            OccurredAt = user.CreatedAt,
        });
        return (user, invitation, token);
    }

    /// <summary>A field user: employee ID + PIN, active at once, no Keycloak account (AC2).</summary>
    public static Result<UserAccount> CreateFieldUser(
        TimeProvider clock, TenantId tenant, string? firstName, string? lastName, string? employeeId, string? pin,
        IReadOnlyList<RoleAssignment>? roles, string? actor, RoleLookup? known = null) =>
        NewFieldUser(clock, tenant, firstName, lastName, employeeId, pin, pinRequired: true, roles, actor, known);

    /// <summary>
    /// A field user without a PIN (Excel import, US-P1-ORG-03: a file never carries PINs): active, but cannot sign in
    /// until an administrator sets the PIN (<see cref="SetPin"/>).
    /// </summary>
    public static Result<UserAccount> CreateFieldUserWithoutPin(
        TimeProvider clock, TenantId tenant, string? firstName, string? lastName, string? employeeId,
        IReadOnlyList<RoleAssignment>? roles, string? actor, RoleLookup? known = null) =>
        NewFieldUser(clock, tenant, firstName, lastName, employeeId, pin: null, pinRequired: false, roles, actor, known);

    /// <summary>The employee ID as stored (trimmed), or the validation error.</summary>
    public static Result<string> CheckEmployeeId(string? employeeId)
    {
        var employee = (employeeId ?? string.Empty).Trim();
        return employee.Length is 0 or > EmployeeIdMaxLength || !EmployeeIdPattern().IsMatch(employee)
            ? Error.Validation("identity.employee_id_invalid", $"The employee ID must be 1–{EmployeeIdMaxLength} letters, digits, '-' or '_'.")
            : employee;
    }

    /// <summary>The contact of an office user as stored (e-mail lower-cased, phone E.164), or the validation error.</summary>
    public static Result<(string? Email, string? Phone)> CheckContact(string? email, string? phone) => Contact(email, phone);

    /// <summary>Sets (or replaces) a field user's PIN; the PIN itself never reaches the ledger.</summary>
    public Result<bool> SetPin(TimeProvider clock, string? pin, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Kind != UserKind.Field)
        {
            return Error.BusinessRule("identity.not_field_user", "Only field users sign in with a PIN; office users have a password.");
        }

        if (Status == UserStatus.Deactivated)
        {
            return Error.BusinessRule("identity.user_deactivated", "A deactivated user cannot get a PIN.");
        }

        var hash = Pin.Hash(pin);
        if (!hash.IsSuccess)
        {
            return hash.Error;
        }

        PinHash = hash.Value;
        PinFailures = 0;
        PinLockedUntil = null;
        ChangedAt = clock.GetUtcNow();
        Raise(new UserPinSet(Id.Value, actor) { OccurredAt = clock.GetUtcNow() });
        return true;
    }

    /// <summary>
    /// The online unlock of a tablet (ADR-0028 §4): the PIN is checked against the stored hash; 5 wrong PINs in a row
    /// lock this user for 15 minutes on every tablet (recorded in the ledger). A locked user is refused even with the
    /// right PIN until the lock ends.
    /// </summary>
    public Result<bool> UnlockWithPin(TimeProvider clock, string? pin, Guid deviceId)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        if (Kind != UserKind.Field || Status != UserStatus.Active || PinHash is null)
        {
            return Error.Unauthorized("identity.unlock_refused", "This person cannot unlock a tablet. Check the employee ID or ask for a PIN.");
        }

        if (PinLockedUntil is { } until && now < until)
        {
            return Error.Unauthorized("identity.pin_locked", "Too many wrong PINs. Try again later.");
        }

        if (Pin.Verify(pin, PinHash))
        {
            PinFailures = 0;
            PinLockedUntil = null;
            return true;
        }

        PinFailures++;
        if (PinFailures >= PinFailuresBeforeLock)
        {
            PinFailures = 0;
            PinLockedUntil = now + PinLockDuration;
            Raise(new UserPinLocked(Id.Value, deviceId, PinLockedUntil.Value) { OccurredAt = now });
            return Error.Unauthorized("identity.pin_locked", "Too many wrong PINs. Try again later.");
        }

        return Error.Unauthorized("identity.pin_wrong", "Wrong PIN.");
    }

    private static Result<UserAccount> NewFieldUser(
        TimeProvider clock, TenantId tenant, string? firstName, string? lastName, string? employeeId, string? pin, bool pinRequired,
        IReadOnlyList<RoleAssignment>? roles, string? actor, RoleLookup? known)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var lookup = known ?? Domain.Roles.Find;
        var common = Validate(firstName, lastName, roles, lookup);
        if (!common.IsSuccess)
        {
            return common.Error;
        }

        var (first, last, assignments) = common.Value;
        if (assignments.Any(a => lookup(a.Role)!.Office))
        {
            return Error.Validation("identity.role_needs_account", "Office roles need an invited account with a password, not a PIN.");
        }

        var employee = CheckEmployeeId(employeeId);
        if (!employee.IsSuccess)
        {
            return employee.Error;
        }

        string? pinHash = null;
        if (pinRequired)
        {
            var hash = Pin.Hash(pin);
            if (!hash.IsSuccess)
            {
                return hash.Error;
            }

            pinHash = hash.Value;
        }

        var user = new UserAccount(EntityId.New<UserId>(clock), tenant, UserKind.Field, first, last, assignments, clock.GetUtcNow())
        {
            Status = UserStatus.Active,
            EmployeeId = employee.Value,
            PinHash = pinHash,
        };
        user.Raise(new FieldUserCreated(user.Id.Value, assignments, actor) { OccurredAt = user.CreatedAt });
        return user;
    }

    /// <summary>Links the Keycloak account created for the invitation.</summary>
    public void LinkAccount(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (Subject is not null && Subject != subject)
        {
            throw new InvalidOperationException($"User {Id} is already linked to another account.");
        }

        Subject = subject;
    }

    public Result<UserAccount> Accept(Invitation invitation, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentNullException.ThrowIfNull(clock);
        if (Status != UserStatus.Invited)
        {
            return Error.Conflict("identity.invitation_used", "This invitation was already used.");
        }

        var accepted = invitation.Accept(clock);
        if (!accepted.IsSuccess)
        {
            return accepted.Error;
        }

        Status = UserStatus.Active;
        ChangedAt = clock.GetUtcNow();
        Raise(new InvitationAccepted(Id.Value, invitation.Id.Value) { OccurredAt = clock.GetUtcNow() });
        return this;
    }

    /// <summary>
    /// Replaces the role assignments (same rules as at creation: known roles, field users only field roles). False
    /// (and no event) when they are the same; a deactivated user keeps theirs.
    /// </summary>
    public Result<bool> ChangeRoles(TimeProvider clock, IReadOnlyList<RoleAssignment>? roles, RoleLookup known, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(known);
        if (Status == UserStatus.Deactivated)
        {
            return Error.BusinessRule("identity.user_deactivated", "A deactivated user's roles cannot change.");
        }

        var checkedRoles = ValidateRoles(roles, known);
        if (!checkedRoles.IsSuccess)
        {
            return checkedRoles.Error;
        }

        var after = checkedRoles.Value;
        if (Kind == UserKind.Field && after.Any(a => known(a.Role)!.Office))
        {
            return Error.Validation("identity.role_needs_account", "Office roles need an invited account with a password, not a PIN.");
        }

        if (after.Count == Roles.Count && after.All(Roles.Contains))
        {
            return false;
        }

        var before = Roles;
        Roles = after;
        ChangedAt = clock.GetUtcNow();
        Raise(new UserRolesChanged(Id.Value, before, after, actor) { OccurredAt = clock.GetUtcNow() });
        return true;
    }

    /// <summary>A new invitation for an invited user (resend, US-P1-IAM-03): the previous link stops working.</summary>
    public Result<(Invitation Invitation, string Token)> Reinvite(TimeProvider clock, Invitation previous, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(previous);
        if (Status != UserStatus.Invited || previous.UserId != Id)
        {
            return Error.BusinessRule("identity.not_invited", "This user already accepted their invitation or was deactivated.");
        }

        previous.Revoke(clock);
        var (invitation, token) = Invitation.Issue(clock, TenantId, Id, previous.Channel);
        Raise(new InvitationReissued(Id.Value, invitation.Id.Value, previous.Id.Value, invitation.ExpiresAt, actor) { OccurredAt = invitation.CreatedAt });
        return (invitation, token);
    }

    /// <summary>Idempotent: deactivating a deactivated user changes nothing and raises nothing.</summary>
    public bool Deactivate(TimeProvider clock, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (Status == UserStatus.Deactivated)
        {
            return false;
        }

        Status = UserStatus.Deactivated;
        DeactivatedAt = clock.GetUtcNow();
        ChangedAt = DeactivatedAt.Value;
        Raise(new UserDeactivated(Id.Value, actor) { OccurredAt = DeactivatedAt.Value });
        return true;
    }

    private static Result<(string First, string Last, List<RoleAssignment> Roles)> Validate(
        string? firstName, string? lastName, IReadOnlyList<RoleAssignment>? roles, RoleLookup known)
    {
        var first = Normalize(firstName);
        var last = Normalize(lastName);
        if (first is null || last is null)
        {
            return Error.Validation("identity.name_invalid", $"First and last name are required (1–{NameMaxLength} characters).");
        }

        var checkedRoles = ValidateRoles(roles, known);
        return checkedRoles.IsSuccess ? (first, last, checkedRoles.Value) : checkedRoles.Error;
    }

    private static Result<List<RoleAssignment>> ValidateRoles(IReadOnlyList<RoleAssignment>? roles, RoleLookup known)
    {
        if (roles is not { Count: > 0 })
        {
            return Error.Validation("identity.roles_required", "Give the user at least one role.");
        }

        if (roles.Any(r => r is null || known(r.Role) is null))
        {
            return Error.Validation("identity.role_unknown", "Unknown role.");
        }

        if (roles.Any(r => r.SiteId == Guid.Empty))
        {
            return Error.Validation("identity.site_invalid", "A site scope must name a site.");
        }

        return roles.Distinct().ToList();
    }

    private static Result<(string? Email, string? Phone)> Contact(string? email, string? phone)
    {
        var mail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        var number = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if ((mail is null) == (number is null))
        {
            return Error.Validation("identity.contact_required", "Invite by e-mail or by phone number (exactly one).");
        }

        if (mail is not null && (mail.Length > 254 || !EmailPattern().IsMatch(mail)))
        {
            return Error.Validation("identity.email_invalid", "The e-mail address is not valid.");
        }

        if (number is not null && !PhonePattern().IsMatch(number))
        {
            return Error.Validation("identity.phone_invalid", "The phone number must be in international format, e.g. +21612345678.");
        }

        return (mail, number);
    }

    private static string? Normalize(string? name)
    {
        var value = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length is 0 or > NameMaxLength || value.Any(char.IsControl) ? null : value;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$")]
    private static partial Regex PhonePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex EmployeeIdPattern();
}
