using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Identity.Application;

/// <summary>
/// The <c>users</c> import (US-P1-ORG-03, ADR-0027 §5): one row per person with one role, on a site or company-wide.
/// An e-mail or phone makes an office user (invited, as on screen), an employee ID a field user (active, without a PIN:
/// a file never carries PINs; an administrator sets it on screen). The key is the e-mail, phone or employee ID. A known
/// user gets the row's role added when missing; nothing is ever removed and names are kept, so the same file again
/// changes nothing. Privileged roles are refused: they are given on screen, where a recent sign-in is required. The
/// commit saves users and invitations in one transaction; <see cref="ImportedUserAccounts"/> then creates the Keycloak
/// accounts and sends the invitations.
/// </summary>
public sealed class UserImport(
    IIdentityStore store, RoleDirectory directory, UserAdministration administration, ISiteDirectory sites, ILimitGuard limits, ICallerAccess caller,
    ITenantContext tenant, TimeProvider clock) : IImportTarget
{
    public static readonly Error KindAmbiguous = Error.Validation(
        "identity.user_kind_ambiguous", "Give an e-mail or a phone for an office user, or an employee ID for a field user, not both.");

    public static readonly Error RolePrivileged = Error.Validation(
        "identity.role_privileged", "This role gives sensitive rights: give it on screen, where your identity is confirmed.");

    public static readonly Error SiteInvalid = Error.Validation("identity.site_invalid", "No active site you manage has this code.");

    private static readonly Dictionary<string, string> ColumnOfError = new(StringComparer.Ordinal)
    {
        ["identity.name_invalid"] = "firstName",
        ["identity.email_invalid"] = "email",
        ["identity.phone_invalid"] = "phone",
        ["identity.contact_required"] = "email",
        ["identity.employee_id_invalid"] = "employeeId",
    };

    public string Kind => "users";

    public string Permission => IdentityPermissions.UsersManage;

    public IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new("firstName", "First name", "Prénom", Required: true, Example: "Sami"),
        new("lastName", "Last name", "Nom", Required: true, Example: "Ben Salah"),
        new("email", "E-mail (office)", "E-mail (bureau)", Required: false, Example: "sami@example.tn"),
        new("phone", "Phone (office)", "Téléphone (bureau)", Required: false, Example: "+21612345678"),
        new("employeeId", "Employee ID (field)", "Matricule (terrain)", Required: false, Example: "OP-042"),
        new("role", "Role", "Rôle", Required: true, List: "roles", Example: "operator"),
        new("site", "Site", "Site", Required: false, List: "sites"),
    ];

    /// <summary>The roles a file may give (not privileged), and the codes of the active sites the importer manages.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken)
    {
        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["roles"] = [.. roles.All.Where(r => !r.Privileged).Select(r => r.Code)],
            ["sites"] = [.. (await ManagedSitesAsync(cancellationToken).ConfigureAwait(false)).Keys.Order(StringComparer.Ordinal)],
        };
    }

    public async Task<ImportValidation> ValidateAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) =>
        (await PrepareAsync(rows, cancellationToken).ConfigureAwait(false)).Validation;

    public async Task<ImportCommit> CommitAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(rows, cancellationToken).ConfigureAwait(false);
        if (prepared.Validation.HasErrors)
        {
            return new ImportCommit(prepared.Validation, 0, 0, 0);
        }

        foreach (var (user, invitation) in prepared.Creates)
        {
            store.Track(user, invitation);
        }

        return await store.TrySaveAllAsync(cancellationToken).ConfigureAwait(false)
            ? ImportCommits.Of(prepared.Validation)
            : ImportCommits.DataChanged(prepared.Validation);
    }

    private TenantId Tenant => tenant.Current ?? throw new InvalidOperationException("Users need a tenant.");

    private async Task<(ImportValidation Validation, List<(UserAccount User, Invitation? Invitation)> Creates)> PrepareAsync(
        IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        var managedSites = await ManagedSitesAsync(cancellationToken).ConfigureAwait(false);
        var keys = rows.ToDictionary(r => r.Number, KeyOf);
        var issues = rows.Where(r => keys[r.Number] is not null).GroupBy(r => keys[r.Number]!.Value.Key, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(r => ImportIssue.Error(r, keys[r.Number]!.Value.Column, ImportCodes.KeyDuplicate, new Dictionary<string, string>
            {
                ["key"] = g.Key,
                ["rows"] = string.Join(", ", g.Select(o => o.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            }))).ToList();
        var duplicated = issues.Select(i => i.Row).ToHashSet();
        var found = keys.Values.OfType<(string Key, string Column, UserKind Kind)>().ToList();
        var existing = await store.FindForImportAsync(
            [.. found.Where(k => k.Column == "email").Select(k => k.Key)], [.. found.Where(k => k.Column == "phone").Select(k => k.Key)],
            [.. found.Where(k => k.Column == "employeeId").Select(k => k.Key)], cancellationToken).ConfigureAwait(false);

        var plans = new List<ImportRowPlan>();
        var creates = new List<(UserAccount, Invitation?)>();
        foreach (var row in rows)
        {
            var rowIssues = Columns.Where(c => c.Required && row[c.Key] is null).Select(c => ImportIssue.Error(row, c.Key, ImportCodes.ValueRequired)).ToList();
            var office = row["email"] is not null || row["phone"] is not null;
            if (office && row["employeeId"] is not null)
            {
                rowIssues.Add(ImportIssue.Error(row, "employeeId", KindAmbiguous));
            }
            else if (!office && row["employeeId"] is null)
            {
                rowIssues.Add(ImportIssue.Error(row, "email", Error.Validation("identity.contact_required", "Give an e-mail, a phone or an employee ID.")));
            }
            else if (keys[row.Number] is null)
            {
                var error = office ? UserAccount.CheckContact(row["email"], row["phone"]).Error! : UserAccount.CheckEmployeeId(row["employeeId"]).Error!;
                rowIssues.Add(ImportIssue.Error(row, ColumnOfError.GetValueOrDefault(error.Code, "email"), error));
            }

            RoleAssignment? assignment = null;
            if (row["role"] is { } roleText)
            {
                var role = roles.Find(roleText.Trim()) ?? roles.Find(roleText.Trim().ToLowerInvariant());
                Guid? siteId = null;
                if (row["site"] is { } siteCode)
                {
                    if (managedSites.TryGetValue(siteCode.Trim(), out var site))
                    {
                        siteId = site;
                    }
                    else
                    {
                        rowIssues.Add(ImportIssue.Error(row, "site", SiteInvalid));
                    }
                }

                if (role is null)
                {
                    rowIssues.Add(ImportIssue.Error(row, "role", Error.Validation("identity.role_unknown", "Unknown role.")));
                }
                else if (role.Privileged)
                {
                    rowIssues.Add(ImportIssue.Error(row, "role", RolePrivileged));
                }
                else if (!office && role.Office)
                {
                    rowIssues.Add(ImportIssue.Error(row, "role", Error.Validation("identity.role_needs_account", "Office roles need an invited account with a password, not a PIN.")));
                }
                else if (rowIssues.All(i => i.Column != "site"))
                {
                    assignment = new RoleAssignment(role.Code, siteId);
                    if (administration.CheckGrant(roles, [assignment]) is { } refused)
                    {
                        rowIssues.Add(ImportIssue.Error(row, assignment.SiteId is null ? "role" : "site", refused));
                        assignment = null;
                    }
                }
            }

            var key = keys[row.Number];
            var user = key is null ? null : existing.FirstOrDefault(u => key.Value.Column switch
            {
                "email" => u.Email == key.Value.Key,
                "phone" => u.Phone == key.Value.Key,
                _ => u.EmployeeId == key.Value.Key,
            });
            if (user is { Status: UserStatus.Deactivated })
            {
                rowIssues.Add(ImportIssue.Error(row, key!.Value.Column, Error.BusinessRule("identity.user_deactivated", "This user was deactivated.")));
            }
            else if (user is not null && !administration.CanManage(user))
            {
                rowIssues.Add(ImportIssue.Error(row, key!.Value.Column, Error.Forbidden("identity.scope_exceeded", "This user also works outside the sites you manage.")));
            }

            if (rowIssues.Count > 0 || duplicated.Contains(row.Number) || key is null || assignment is null)
            {
                issues.AddRange(rowIssues);
                continue;
            }

            if (user is not null)
            {
                var changed = user.Roles.Contains(assignment) ? false : user.ChangeRoles(clock, [.. user.Roles, assignment], roles.Lookup, caller.Subject).Value;
                plans.Add(new ImportRowPlan(row.Number, key.Value.Key, changed ? ImportAction.Update : ImportAction.Unchanged));
                continue;
            }

            if (key.Value.Kind == UserKind.Office)
            {
                var invited = UserAccount.Invite(clock, Tenant, row["firstName"], row["lastName"], row["email"], row["phone"], [assignment], caller.Subject, roles.Lookup);
                if (!invited.IsSuccess)
                {
                    issues.Add(ImportIssue.Error(row, ColumnOfError.GetValueOrDefault(invited.Error!.Code, "firstName"), invited.Error!));
                    continue;
                }

                // The token is dropped: ImportedUserAccounts issues the delivered one once the Keycloak account exists.
                creates.Add((invited.Value.User, invited.Value.Invitation));
            }
            else
            {
                var created = UserAccount.CreateFieldUserWithoutPin(clock, Tenant, row["firstName"], row["lastName"], row["employeeId"], [assignment], caller.Subject, roles.Lookup);
                if (!created.IsSuccess)
                {
                    issues.Add(ImportIssue.Error(row, ColumnOfError.GetValueOrDefault(created.Error!.Code, "firstName"), created.Error!));
                    continue;
                }

                creates.Add((created.Value, null));
            }

            plans.Add(new ImportRowPlan(row.Number, key.Value.Key, ImportAction.Create));
        }

        var offices = creates.Count(c => c.Item1.Kind == UserKind.Office);
        if (offices > 0 && await limits.CheckCreateAsync(Metrics.OfficeUsers, offices, cancellationToken).ConfigureAwait(false) is { } full)
        {
            issues.Add(ImportIssue.ForFile(full));
        }

        return (new ImportValidation(issues, plans), creates);
    }

    /// <summary>The row's key as stored (e-mail lower-cased, phone, or employee ID), its column and the user kind; null when unusable.</summary>
    private static (string Key, string Column, UserKind Kind)? KeyOf(ImportRow row)
    {
        var office = row["email"] is not null || row["phone"] is not null;
        if (office == (row["employeeId"] is not null))
        {
            return null;
        }

        if (!office)
        {
            return UserAccount.CheckEmployeeId(row["employeeId"]) is { IsSuccess: true } employee ? (employee.Value, "employeeId", UserKind.Field) : null;
        }

        if (UserAccount.CheckContact(row["email"], row["phone"]) is not { IsSuccess: true } contact)
        {
            return null;
        }

        return contact.Value.Email is { } email ? (email, "email", UserKind.Office) : (contact.Value.Phone!, "phone", UserKind.Office);
    }

    /// <summary>Code → ID of the active sites where the importer manages users (codes compared without case).</summary>
    private async Task<Dictionary<string, Guid>> ManagedSitesAsync(CancellationToken cancellationToken)
    {
        var manage = caller.Current.ScopeOf(IdentityPermissions.UsersManage);
        return (await sites.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(s => !s.Archived && manage.CoversSite(s.Id))
            .ToDictionary(s => s.Code, s => s.Id, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Second step of a users import (ADR-0027 §5): for an invited user saved without a Keycloak account (only the import
/// does that; the API links the account before saving), creates the account, issues a fresh invitation (the first
/// token was never sent) and delivers it. Runs from the Identity outbox, retried until done; an account that already
/// belongs to someone else in Keycloak cannot be fixed by retrying, so it is logged and left (the user stays invited).
/// </summary>
public sealed partial class ImportedUserAccounts(
    IIdentityStore store, IIdentityProvider provider, IInvitationSender sender, RoleDirectory directory, ITenantDirectory tenants,
    IOptions<IdentityOptions> options, TimeProvider clock, ILogger<ImportedUserAccounts> logger) : IDomainEventSubscriber<UserInvited>
{
    public async Task HandleAsync(UserInvited domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(context);
        var user = await store.FindUserAsync(new UserId(domainEvent.UserId), cancellationToken).ConfigureAwait(false);
        if (user is null || user.Subject is not null || user.Status != UserStatus.Invited)
        {
            return;
        }

        var roles = await directory.LoadAsync(cancellationToken).ConfigureAwait(false);
        var username = user.Email ?? user.Phone!.TrimStart('+');
        var account = await provider.CreateAccountAsync(
            new NewAccount(user.TenantId, username, user.Email, user.FirstName, user.LastName, user.RequiresMfa(roles.Lookup)), cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            if (account.Error!.Code == "identity.account_exists")
            {
                LogAccountTaken(logger, user.Id.Value);
                return;
            }

            throw new InvalidOperationException($"Keycloak account of imported user {user.Id} not created: {account.Error.Code}.");
        }

        user.LinkAccount(account.Value);
        var previous = await store.FindOpenInvitationAsync(user.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Imported user {user.Id} has no open invitation.");
        var (invitation, token) = user.Reinvite(clock, previous, domainEvent.Actor).Value;
        store.Add(invitation);
        var saved = await store.SaveAsync(cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            throw new InvalidOperationException($"Imported user {user.Id} changed meanwhile: {saved.Error!.Code}.");
        }

        var company = await tenants.FindAsync(user.TenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {user.TenantId} is not in the catalog.");
        await sender.SendAsync(InviteUserHandler.MessageFor(options.Value, company, user, invitation, token), cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 2110, Level = LogLevel.Warning,
        Message = "Imported user {UserId}: the Keycloak username belongs to another account; the user stays invited without an account.")]
    private static partial void LogAccountTaken(ILogger logger, Guid userId);
}
