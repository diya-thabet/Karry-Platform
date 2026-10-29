using Karry.BuildingBlocks.Authorization;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Identity.Application;

/// <summary>Permissions of the Identity module (role matrix row "Users, roles, masking, policies").</summary>
public static class IdentityPermissions
{
    /// <summary>See users: all of them tenant-wide, or the users holding a role on the caller's sites.</summary>
    public const string UsersRead = "identity.users.read";

    /// <summary>Invite, create field users, deactivate, within the caller's scope (US-P1-IAM-03).</summary>
    public const string UsersManage = "identity.users.manage";

    /// <summary>See roles, their permissions and field levels.</summary>
    public const string RolesRead = "identity.roles.read";

    /// <summary>Create, clone, change and delete custom roles; set field levels (US-P1-IAM-05, US-P1-IAM-07 AC1).</summary>
    public const string RolesManage = "identity.roles.manage";

    /// <summary>Enrol site tablets, see and revoke them, on the caller's sites (US-P1-OFF-02).</summary>
    public const string DevicesManage = "identity.devices.manage";

    /// <summary>Create support codes for Karry support, see and end support sessions (ADR-0029 §4, US-P1-OPS-03 AC3).</summary>
    public const string SupportManage = "identity.support.manage";

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(UsersRead, ScopeKind.Site, privileged: false, Roles.TenantAdmin, Roles.Executive, Roles.SiteManager),
        new(UsersManage, ScopeKind.Site, privileged: true, Roles.TenantAdmin, Roles.SiteManager),
        new(RolesRead, ScopeKind.Tenant, privileged: false, Roles.TenantAdmin, Roles.Executive),
        new(RolesManage, ScopeKind.Tenant, privileged: true, Roles.TenantAdmin),
        new(DevicesManage, ScopeKind.Site, privileged: true, Roles.TenantAdmin, Roles.SiteManager),
        new(SupportManage, ScopeKind.Tenant, privileged: true, Roles.TenantAdmin),
    ];
}

/// <summary>
/// Who may see and change which users (US-P1-IAM-06, threat T-13). Scope: a tenant-wide holder covers every user; a
/// holder on some sites sees users with a role on one of them and manages users whose roles are all on them.
/// Granting: within the caller's scope, never a privileged permission the caller lacks there, and a privileged role
/// (second factor) only by someone who holds everything that role contains there.
/// </summary>
public sealed class UserAdministration(ICallerAccess caller, AuthorizationCatalog catalog)
{
    public static readonly Error NotFound = Error.NotFound("identity.user_not_found", "No such user in this company.");

    public CallerAccess Access => caller.Current;

    public string? Subject => caller.Subject;

    public bool CanSee(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var scope = Access.ScopeOf(IdentityPermissions.UsersRead);
        return scope.TenantWide || user.Roles.Any(r => r.SiteId is { } site && scope.Sites.Contains(site));
    }

    public bool CanManage(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var scope = Access.ScopeOf(IdentityPermissions.UsersManage);
        return scope.TenantWide || (user.Roles.Count > 0 && user.Roles.All(r => r.SiteId is { } site && scope.Sites.Contains(site)));
    }

    /// <summary>Null when the caller may give these role assignments (default or custom roles of the tenant); otherwise the reason.</summary>
    public Error? CheckGrant(TenantRoles roles, IReadOnlyList<RoleAssignment>? assignments)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var manage = Access.ScopeOf(IdentityPermissions.UsersManage);
        foreach (var assignment in assignments ?? [])
        {
            if (assignment is null || roles.Find(assignment.Role) is not { } role)
            {
                continue; // the domain rejects unknown roles with a validation error
            }

            var inScope = assignment.SiteId is { } site ? manage.CoversSite(site) : manage.TenantWide;
            if (!inScope)
            {
                return Error.Forbidden("identity.scope_exceeded", "You can only give roles within the sites you manage.");
            }

            // What this assignment would give, permission by permission, compared with what the caller holds there.
            var granted = CallerAccess.From(catalog, [roles.Resolve(assignment)]);
            foreach (var code in granted.Granted)
            {
                var privileged = catalog.Permission(code)!.Privileged || role.Privileged;
                if (privileged && !Contains(Access.ScopeOf(code), granted.ScopeOf(code)))
                {
                    return Error.Forbidden("identity.escalation", "You cannot give a role with rights you do not have yourself.");
                }
            }
        }

        return null;
    }

    private static bool Contains(ScopeSet holder, ScopeSet given) =>
        holder.TenantWide || (!given.TenantWide && given.Sites.IsSubsetOf(holder.Sites) && given.Warehouses.IsSubsetOf(holder.Warehouses));
}
