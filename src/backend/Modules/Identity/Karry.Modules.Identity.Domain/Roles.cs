using System.Collections.Frozen;

namespace Karry.Modules.Identity.Domain;

/// <summary>A default role of the catalogue (docs/03-domain/catalogues/roles-and-permissions.md).</summary>
/// <param name="Code">Stable identifier stored on users and in ledger events.</param>
/// <param name="Privileged">Gives access to money, people or the audit trail: a second factor is mandatory (US-P1-IAM-02).</param>
/// <param name="Office">Signs in with the portal (counts as an office seat); otherwise a field role (PIN on a tablet).</param>
public sealed record RoleDefinition(string Code, bool Privileged, bool Office);

/// <summary>
/// The 12 default roles. P1.8 assigns them to users (with a site scope) and derives MFA from them; their permissions,
/// custom roles and cloning arrive in P1.9 (US-P1-IAM-04/05).
/// </summary>
public static class Roles
{
    public const string TenantAdmin = "tenant-admin";
    public const string Executive = "executive";
    public const string Finance = "finance";
    public const string Controller = "controller";
    public const string SiteManager = "site-manager";
    public const string MaintenancePlanner = "maintenance-planner";
    public const string Commercial = "commercial";
    public const string Storekeeper = "storekeeper";
    public const string Weighmaster = "weighmaster";
    public const string FuelAttendant = "fuel-attendant";
    public const string Mechanic = "mechanic";
    public const string Operator = "operator";

    public static readonly FrozenDictionary<string, RoleDefinition> Defaults = new RoleDefinition[]
    {
        new(TenantAdmin, Privileged: true, Office: true),
        new(Executive, Privileged: true, Office: true),
        new(Finance, Privileged: true, Office: true),
        new(Controller, Privileged: true, Office: true),
        new(SiteManager, Privileged: false, Office: true),
        new(MaintenancePlanner, Privileged: false, Office: true),
        new(Commercial, Privileged: false, Office: true),
        new(Storekeeper, Privileged: false, Office: false),
        new(Weighmaster, Privileged: false, Office: false),
        new(FuelAttendant, Privileged: false, Office: false),
        new(Mechanic, Privileged: false, Office: false),
        new(Operator, Privileged: false, Office: false),
    }.ToFrozenDictionary(r => r.Code, StringComparer.Ordinal);
}

/// <summary>A role held tenant-wide (<see cref="SiteId"/> null) or on one site (US-P1-IAM-03 AC1, US-P1-IAM-06).</summary>
public sealed record RoleAssignment(string Role, Guid? SiteId);
