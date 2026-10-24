using System.Collections.Frozen;
using Karry.BuildingBlocks.Modules;
using Karry.SharedKernel.Authorization;

namespace Karry.BuildingBlocks.Authorization;

/// <summary>
/// Every permission and sensitive field the modules declare (ADR-0022). Built once at start-up from the module list;
/// a declaration error (duplicate code, code of another module, unknown default role) stops the host.
/// </summary>
public sealed class AuthorizationCatalog
{
    public static readonly AuthorizationCatalog Empty = new([], []);

    private readonly FrozenDictionary<string, PermissionDefinition> permissions;
    private readonly FrozenDictionary<string, SensitiveFieldDefinition> fields;

    private AuthorizationCatalog(IEnumerable<PermissionDefinition> permissions, IEnumerable<SensitiveFieldDefinition> fields)
    {
        this.permissions = permissions.ToFrozenDictionary(p => p.Code, StringComparer.Ordinal);
        this.fields = fields.ToFrozenDictionary(f => f.Code, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<PermissionDefinition> Permissions => permissions.Values;

    public IReadOnlyCollection<SensitiveFieldDefinition> SensitiveFields => fields.Values;

    public PermissionDefinition? Permission(string code) => permissions.GetValueOrDefault(code);

    public SensitiveFieldDefinition? SensitiveField(string code) => fields.GetValueOrDefault(code);

    /// <summary>The permissions a default role holds (its row of the role matrix).</summary>
    public IReadOnlySet<string> DefaultPermissionsOf(string role) =>
        permissions.Values.Where(p => p.DefaultRoles.Contains(role, StringComparer.Ordinal)).Select(p => p.Code).ToFrozenSet(StringComparer.Ordinal);

    public static AuthorizationCatalog From(IReadOnlyList<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var problems = new List<string>();
        var permissions = new List<PermissionDefinition>();
        var fields = new List<SensitiveFieldDefinition>();
        foreach (var module in modules)
        {
            foreach (var permission in module.Permissions)
            {
                Check(problems, module.Name, permission.Code, permission.Module, permission.DefaultRoles);
                permissions.Add(permission);
            }

            foreach (var field in module.SensitiveFields)
            {
                Check(problems, module.Name, field.Code, field.Module, field.Defaults.Keys);
                fields.Add(field);
            }
        }

        problems.AddRange(permissions.Select(p => p.Code).Concat(fields.Select(f => f.Code))
            .GroupBy(code => code, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"'{g.Key}' is declared more than once."));
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Invalid authorization declarations: " + string.Join(" ", problems));
        }

        return new AuthorizationCatalog(permissions, fields);
    }

    private static void Check(List<string> problems, string module, string code, string prefix, IEnumerable<string> roles)
    {
        if (prefix != module)
        {
            problems.Add($"'{code}' is declared by module '{module}' but names module '{prefix}'.");
        }

        problems.AddRange(roles.Where(role => !DefaultRoles.All.Contains(role, StringComparer.Ordinal)).Select(role => $"'{code}' names unknown default role '{role}'."));
    }
}
