using System.Collections.Frozen;
using Karry.SharedKernel.Authorization;

namespace Karry.BuildingBlocks.Authorization;

/// <summary>
/// One role a person holds at one scope (US-P1-IAM-06 AC1), resolved to what the role contains: its permissions and
/// its level for every sensitive field. Tenant-wide when both <see cref="SiteId"/> and <see cref="WarehouseId"/> are null.
/// </summary>
public sealed record AccessAssignment(
    string Role, IReadOnlySet<string> Permissions, IReadOnlyDictionary<string, FieldAccess> Fields, Guid? SiteId = null, Guid? WarehouseId = null)
{
    public bool TenantWide => SiteId is null && WarehouseId is null;
}

/// <summary>Where a permission applies: everywhere, or some sites and warehouses. Empty = not granted.</summary>
public sealed class ScopeSet
{
    public static readonly ScopeSet None = new(false, FrozenSet<Guid>.Empty, FrozenSet<Guid>.Empty);

    public static readonly ScopeSet Everywhere = new(true, FrozenSet<Guid>.Empty, FrozenSet<Guid>.Empty);

    private ScopeSet(bool tenantWide, IReadOnlySet<Guid> sites, IReadOnlySet<Guid> warehouses)
    {
        TenantWide = tenantWide;
        Sites = sites;
        Warehouses = warehouses;
    }

    /// <summary>Every site and warehouse of the company, present and future.</summary>
    public bool TenantWide { get; }

    public IReadOnlySet<Guid> Sites { get; }

    /// <summary>Warehouses granted one by one (warehouses of <see cref="Sites"/> are covered as well).</summary>
    public IReadOnlySet<Guid> Warehouses { get; }

    public bool IsEmpty => !TenantWide && Sites.Count == 0 && Warehouses.Count == 0;

    public bool CoversSite(Guid siteId) => TenantWide || Sites.Contains(siteId);

    public bool CoversWarehouse(Guid warehouseId, Guid siteOfWarehouse) =>
        TenantWide || Warehouses.Contains(warehouseId) || Sites.Contains(siteOfWarehouse);

    internal static ScopeSet Of(IEnumerable<Guid> sites, IEnumerable<Guid> warehouses)
    {
        var siteSet = sites.ToFrozenSet();
        var warehouseSet = warehouses.ToFrozenSet();
        return siteSet.Count == 0 && warehouseSet.Count == 0 ? None : new ScopeSet(false, siteSet, warehouseSet);
    }
}

/// <summary>
/// What the caller may do and see in the current tenant, computed from their role assignments (ADR-0022). Immutable;
/// one per request. Unknown or withdrawn permissions in an assignment grant nothing.
/// </summary>
public sealed class CallerAccess
{
    /// <summary>No Karry user, deactivated user, or no role: nothing is allowed, every sensitive field is hidden.</summary>
    public static readonly CallerAccess Nobody = new(FrozenDictionary<string, ScopeSet>.Empty, []);

    private readonly FrozenDictionary<string, ScopeSet> grants;

    private CallerAccess(FrozenDictionary<string, ScopeSet> grants, IReadOnlyList<AccessAssignment> assignments)
    {
        this.grants = grants;
        Assignments = assignments;
    }

    public IReadOnlyList<AccessAssignment> Assignments { get; }

    /// <summary>Permission codes granted at some scope.</summary>
    public IEnumerable<string> Granted => grants.Keys;

    public bool Has(string permission) => !ScopeOf(permission).IsEmpty;

    /// <summary>Where <paramref name="permission"/> applies; list queries filter by it (US-P1-IAM-06 AC2).</summary>
    public ScopeSet ScopeOf(string permission) => grants.GetValueOrDefault(permission, ScopeSet.None);

    /// <summary>
    /// The caller's level for a sensitive field of an object of <paramref name="siteId"/>: the highest level among the
    /// roles that apply there (tenant-wide roles and roles on that site). For an object of no site (a company-wide
    /// object, or one that forgot <see cref="ISiteScoped"/>), only tenant-wide roles count: forgetting the site
    /// over-masks, it never leaks. No applicable role: <see cref="FieldAccess.Hidden"/>.
    /// </summary>
    public FieldAccess FieldAccessFor(string field, Guid? siteId)
    {
        var level = FieldAccess.Hidden;
        foreach (var assignment in Assignments)
        {
            var applies = assignment.TenantWide || (siteId is { } site && assignment.SiteId == site);
            if (applies && assignment.Fields.TryGetValue(field, out var granted) && granted > level)
            {
                level = granted;
            }
        }

        return level;
    }

    /// <summary>The built-in role of a support session (ADR-0029 §4); no company can create or hold it.</summary>
    public const string SupportRole = "support";

    /// <summary>
    /// What Karry support may do in a support session: every read permission (<c>module.resource.read</c>) company-wide,
    /// nothing else, and every sensitive field masked, whatever the company's own roles see.
    /// </summary>
    public static CallerAccess Support(AuthorizationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var reads = catalog.Permissions.Select(p => p.Code).Where(code => code.EndsWith(".read", StringComparison.Ordinal)).ToFrozenSet(StringComparer.Ordinal);
        var masked = catalog.SensitiveFields.ToFrozenDictionary(f => f.Code, _ => FieldAccess.Masked, StringComparer.Ordinal);
        return From(catalog, [new AccessAssignment(SupportRole, reads, masked)]);
    }

    public static CallerAccess From(AuthorizationCatalog catalog, IReadOnlyList<AccessAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count == 0)
        {
            return Nobody;
        }

        var tenantWide = new HashSet<string>(StringComparer.Ordinal);
        var sites = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        var warehouses = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            foreach (var code in assignment.Permissions)
            {
                switch (catalog.Permission(code)?.Scope, assignment)
                {
                    case (null, _):
                        break; // withdrawn from the catalogue: grants nothing
                    case (_, { TenantWide: true }):
                        tenantWide.Add(code);
                        break;
                    case (ScopeKind.Site or ScopeKind.Warehouse, { SiteId: { } site }):
                        Add(sites, code, site);
                        break;
                    case (ScopeKind.Warehouse, { WarehouseId: { } warehouse }):
                        Add(warehouses, code, warehouse);
                        break;
                    default:
                        break; // a tenant permission from a site role, a site permission from a warehouse role: nothing
                }
            }
        }

        var grants = tenantWide.Concat(sites.Keys).Concat(warehouses.Keys).Distinct(StringComparer.Ordinal).ToFrozenDictionary(
            code => code,
            code => tenantWide.Contains(code)
                ? ScopeSet.Everywhere
                : ScopeSet.Of(sites.GetValueOrDefault(code) ?? [], warehouses.GetValueOrDefault(code) ?? []),
            StringComparer.Ordinal);
        return new CallerAccess(grants, assignments);
    }

    private static void Add(Dictionary<string, List<Guid>> into, string code, Guid id)
    {
        if (!into.TryGetValue(code, out var list))
        {
            into[code] = list = [];
        }

        list.Add(id);
    }
}
