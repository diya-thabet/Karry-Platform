namespace Karry.SharedKernel.Authorization;

/// <summary>
/// Codes of the 12 default roles (docs/03-domain/catalogues/roles-and-permissions.md). Modules name them in the
/// default grants of their permissions and sensitive fields; the Identity module owns the roles themselves.
/// </summary>
public static class DefaultRoles
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

    public static IReadOnlyList<string> All { get; } =
    [
        TenantAdmin, Executive, Finance, Controller, SiteManager, MaintenancePlanner, Commercial, Storekeeper, Weighmaster,
        FuelAttendant, Mechanic, Operator,
    ];
}
