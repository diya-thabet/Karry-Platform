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
public sealed record UserInvited(Guid UserId, Guid InvitationId, string Channel, IReadOnlyList<RoleAssignment> Roles, DateTimeOffset ExpiresAt, string? Actor) : DomainEvent;

[EventType("identity.invitation_accepted.v1")]
public sealed record InvitationAccepted(Guid UserId, Guid InvitationId) : DomainEvent;

[EventType("identity.field_user_created.v1")]
public sealed record FieldUserCreated(Guid UserId, IReadOnlyList<RoleAssignment> Roles, string? Actor) : DomainEvent;

[EventType("identity.user_deactivated.v1")]
public sealed record UserDeactivated(Guid UserId, string? Actor) : DomainEvent;

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

    public IReadOnlyList<RoleAssignment> Roles { get; private set; }

    /// <summary>Some role demands a second factor (Keycloak role <c>mfa-required</c>); unknown roles count as privileged.</summary>
    public bool RequiresMfa(RoleLookup roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return Roles.Any(r => roles(r.Role)?.Privileged ?? true);
    }

    public DateTimeOffset CreatedAt { get; private set; }

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
        IReadOnlyList<RoleAssignment>? roles, string? actor, RoleLookup? known = null)
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

        var employee = (employeeId ?? string.Empty).Trim();
        if (employee.Length is 0 or > EmployeeIdMaxLength || !EmployeeIdPattern().IsMatch(employee))
        {
            return Error.Validation("identity.employee_id_invalid", $"The employee ID must be 1–{EmployeeIdMaxLength} letters, digits, '-' or '_'.");
        }

        var hash = Pin.Hash(pin);
        if (!hash.IsSuccess)
        {
            return hash.Error;
        }

        var user = new UserAccount(EntityId.New<UserId>(clock), tenant, UserKind.Field, first, last, assignments, clock.GetUtcNow())
        {
            Status = UserStatus.Active,
            EmployeeId = employee,
            PinHash = hash.Value,
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
        Raise(new InvitationAccepted(Id.Value, invitation.Id.Value) { OccurredAt = clock.GetUtcNow() });
        return this;
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

        return (first, last, roles.Distinct().ToList());
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
