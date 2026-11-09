using Karry.BuildingBlocks.Authorization;
using Karry.Modules.Identity.Domain;
using Karry.SharedKernel.Authorization;

namespace Karry.Modules.Identity.Application;

/// <summary>
/// A role as the tenant sees it (US-P1-IAM-05, US-P1-IAM-07 AC1): its permissions and the effective level of every
/// sensitive field the modules declare. Default roles have no stored name (the apps translate the code). <c>Version</c>
/// is the custom role's, or the default role's settings (0 while the company never changed them), for <c>If-Match</c>.
/// </summary>
public sealed record RoleView(
    string Code, bool IsDefault, string? Name, string? Description, string? BasedOn, bool Office, bool Privileged,
    IReadOnlySet<string> Permissions, IReadOnlyDictionary<string, FieldAccess> FieldLevels, uint Version = 0)
{
    public RoleDefinition Definition => new(Code, Privileged, Office);
}

/// <summary>
/// The roles of the current tenant: the 12 default roles (permissions from the module catalogue, field levels from the
/// catalogue with the tenant's overrides) and its custom roles. A custom role is privileged (second factor, step-up)
/// when it holds a privileged permission or shows a sensitive field; permissions a module no longer declares are ignored.
/// </summary>
public sealed class TenantRoles
{
    private readonly Dictionary<string, RoleView> roles;

    private TenantRoles(IEnumerable<RoleView> roles) => this.roles = roles.ToDictionary(r => r.Code, StringComparer.Ordinal);

    public IEnumerable<RoleView> All => roles.Values.OrderBy(r => !r.IsDefault).ThenBy(r => r.Name ?? r.Code, StringComparer.Ordinal);

    public static TenantRoles Build(AuthorizationCatalog catalog, IEnumerable<CustomRole> custom, IEnumerable<DefaultRoleSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(custom);
        ArgumentNullException.ThrowIfNull(settings);
        var stored = settings.ToDictionary(s => s.Role, StringComparer.Ordinal);
        var defaults = Roles.Defaults.Values.Select(role => new RoleView(
            role.Code, IsDefault: true, Name: null, Description: null, BasedOn: null, role.Office, role.Privileged,
            catalog.DefaultPermissionsOf(role.Code),
            EffectiveDefaultLevels(catalog, role.Code, stored.GetValueOrDefault(role.Code)?.FieldLevels),
            stored.GetValueOrDefault(role.Code)?.Version ?? 0));
        var customs = custom.Select(role =>
        {
            var permissions = role.Permissions.Where(code => catalog.Permission(code) is not null).ToHashSet(StringComparer.Ordinal);
            var fields = EffectiveCustomLevels(catalog, role.FieldLevels);
            return new RoleView(role.Code, IsDefault: false, role.Name, role.Description, role.BasedOn, role.Office,
                IsPrivileged(catalog, permissions, fields), permissions, fields, role.Version);
        });
        return new TenantRoles(defaults.Concat(customs));
    }

    public RoleView? Find(string code) => roles.GetValueOrDefault(code);

    /// <summary>The <see cref="RoleLookup"/> of this tenant (pass as <c>roles.Lookup</c>).</summary>
    public RoleDefinition? Lookup(string code) => Find(code)?.Definition;

    /// <summary>What one assignment gives; a role the tenant no longer has gives nothing (fail closed).</summary>
    public AccessAssignment Resolve(RoleAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return Find(assignment.Role) is { } role
            ? new AccessAssignment(role.Code, role.Permissions, role.FieldLevels, assignment.SiteId)
            : new AccessAssignment(assignment.Role, new HashSet<string>(), new Dictionary<string, FieldAccess>(), assignment.SiteId);
    }

    public IReadOnlyList<AccessAssignment> Of(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Status == UserStatus.Active ? [.. user.Roles.Select(Resolve)] : [];
    }

    /// <summary>A default role's level of every declared field: the tenant's override, else the catalogue's default.</summary>
    public static Dictionary<string, FieldAccess> EffectiveDefaultLevels(
        AuthorizationCatalog catalog, string role, IReadOnlyDictionary<string, FieldAccess>? overrides)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.SensitiveFields.ToDictionary(
            f => f.Code, f => overrides is not null && overrides.TryGetValue(f.Code, out var level) ? level : f.DefaultFor(role), StringComparer.Ordinal);
    }

    /// <summary>A custom role's level of every declared field: its own, else the field's fallback (masked by default).</summary>
    public static Dictionary<string, FieldAccess> EffectiveCustomLevels(AuthorizationCatalog catalog, IReadOnlyDictionary<string, FieldAccess> levels)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(levels);
        return catalog.SensitiveFields.ToDictionary(
            f => f.Code, f => levels.TryGetValue(f.Code, out var level) ? level : f.Fallback, StringComparer.Ordinal);
    }

    public static bool IsPrivileged(AuthorizationCatalog catalog, IEnumerable<string> permissions, IReadOnlyDictionary<string, FieldAccess> fields)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fields);
        return permissions.Any(code => catalog.Permission(code)?.Privileged == true) || fields.Values.Any(level => level >= FieldAccess.Read);
    }
}

/// <summary>Loads <see cref="TenantRoles"/> for the current tenant (fresh on every call: handlers compare before and after).</summary>
public sealed class RoleDirectory(IRoleStore store, AuthorizationCatalog catalog)
{
    public AuthorizationCatalog Catalog => catalog;

    public async Task<TenantRoles> LoadAsync(CancellationToken cancellationToken)
    {
        var custom = await store.CustomRolesAsync(cancellationToken).ConfigureAwait(false);
        var settings = await store.DefaultRoleSettingsAsync(cancellationToken).ConfigureAwait(false);
        return TenantRoles.Build(catalog, custom, settings);
    }
}
