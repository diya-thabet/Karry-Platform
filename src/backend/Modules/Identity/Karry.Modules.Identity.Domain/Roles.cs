using System.Collections.Frozen;
using Karry.SharedKernel.Authorization;

namespace Karry.Modules.Identity.Domain;

/// <summary>A default role of the catalogue (docs/03-domain/catalogues/roles-and-permissions.md).</summary>
/// <param name="Code">Stable identifier stored on users and in ledger events.</param>
/// <param name="Privileged">Gives access to money, people or the audit trail: a second factor is mandatory (US-P1-IAM-02).</param>
/// <param name="Office">Signs in with the portal (counts as an office seat); otherwise a field role (PIN on a tablet).</param>
public sealed record RoleDefinition(string Code, bool Privileged, bool Office);

/// <summary>
/// The 12 default roles (codes in <see cref="DefaultRoles"/>). Their permissions are what the modules declare for them
/// (<c>AuthorizationCatalog</c>, ADR-0022); here: which ones need a second factor and which sign in to the portal.
/// </summary>
public static class Roles
{
    public const string TenantAdmin = DefaultRoles.TenantAdmin;
    public const string Executive = DefaultRoles.Executive;
    public const string Finance = DefaultRoles.Finance;
    public const string Controller = DefaultRoles.Controller;
    public const string SiteManager = DefaultRoles.SiteManager;
    public const string MaintenancePlanner = DefaultRoles.MaintenancePlanner;
    public const string Commercial = DefaultRoles.Commercial;
    public const string Storekeeper = DefaultRoles.Storekeeper;
    public const string Weighmaster = DefaultRoles.Weighmaster;
    public const string FuelAttendant = DefaultRoles.FuelAttendant;
    public const string Mechanic = DefaultRoles.Mechanic;
    public const string Operator = DefaultRoles.Operator;

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
