using System.Text.RegularExpressions;

namespace Karry.SharedKernel.Authorization;

/// <summary>What a resource belongs to, so which role assignments can grant a permission on it (US-P1-IAM-06).</summary>
public enum ScopeKind
{
    /// <summary>Company-wide resource (roles, billing, field policies): only tenant-wide assignments grant it.</summary>
    Tenant = 1,

    /// <summary>Belongs to a site: a tenant-wide assignment grants every site, a site assignment that site.</summary>
    Site = 2,

    /// <summary>Belongs to a warehouse: tenant-wide → all, site → the warehouses of that site, warehouse → that one.</summary>
    Warehouse = 3,
}

/// <summary>
/// A permission a module declares in code (ADR-0022): <c>module.resource.action</c>, the kind of scope it applies to,
/// whether it is privileged (money, people, audit trail: holders need a second factor, granting it needs a recent
/// sign-in) and the default roles that hold it (the matrix in docs/03-domain/catalogues/roles-and-permissions.md).
/// </summary>
public sealed partial record PermissionDefinition
{
    public PermissionDefinition(string code, ScopeKind scope, bool privileged, params string[] defaultRoles)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(defaultRoles);
        if (!CodePattern().IsMatch(code))
        {
            throw new ArgumentException($"Permission '{code}' must be 'module.resource.action' (lowercase, digits, '-').", nameof(code));
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        Code = code;
        Scope = scope;
        Privileged = privileged;
        DefaultRoles = defaultRoles;
    }

    public string Code { get; }

    /// <summary>The module part of the code; must be the declaring module's name.</summary>
    public string Module => Code[..Code.IndexOf('.', StringComparison.Ordinal)];

    public ScopeKind Scope { get; }

    public bool Privileged { get; }

    public IReadOnlyList<string> DefaultRoles { get; }

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*){2}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// How a role sees a sensitive field (US-P1-IAM-07). Ordered: with several roles the highest level wins.
/// </summary>
public enum FieldAccess
{
    /// <summary>Not sent at all; the UI does not show the column.</summary>
    Hidden = 0,

    /// <summary>Not sent; its name is listed in <c>maskedFields</c> and the UI shows <c>•••</c> "hidden by policy".</summary>
    Masked = 1,

    Read = 2,

    /// <summary>Read and change (when the role also holds the permission to change the resource).</summary>
    Write = 3,
}

/// <summary>
/// A configurable sensitive field (unit price, cost rate, margin, wage, loan amount …) a module declares in code,
/// with its default level per default role and for every other role (custom roles created from scratch).
/// </summary>
public sealed partial record SensitiveFieldDefinition
{
    public SensitiveFieldDefinition(string code, IReadOnlyDictionary<string, FieldAccess> defaults, FieldAccess fallback = FieldAccess.Masked)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(defaults);
        if (!CodePattern().IsMatch(code))
        {
            throw new ArgumentException($"Sensitive field '{code}' must be 'module.resource.field' (lowercase, digits, '-').", nameof(code));
        }

        Code = code;
        Defaults = defaults;
        Fallback = fallback;
    }

    public string Code { get; }

    public string Module => Code[..Code.IndexOf('.', StringComparison.Ordinal)];

    public IReadOnlyDictionary<string, FieldAccess> Defaults { get; }

    public FieldAccess Fallback { get; }

    public FieldAccess DefaultFor(string role) => Defaults.TryGetValue(role, out var level) ? level : Fallback;

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*){2}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// Marks a property of an API response or request as the sensitive field <paramref name="field"/>: the JSON
/// serializer sends it only to roles that may read it, and refuses a value from roles that may not write it.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class SensitiveAttribute(string field) : Attribute
{
    public string Field { get; } = field;
}

/// <summary>An API object that belongs to a site: its sensitive fields follow the caller's roles on that site.</summary>
public interface ISiteScoped
{
    Guid? SiteId { get; }
}
