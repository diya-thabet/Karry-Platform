namespace Karry.SharedKernel.Entitlements;

/// <summary>
/// Codes of the features a plan or add-on can include (ADR-0023, business model §2.3). Plans are data, but the codes
/// they name are declared here, so a plan naming an unknown feature is refused. Modules gate endpoints with
/// <c>.RequiresFeature(Features.X)</c>; a module that ships later already finds its code.
/// </summary>
public static class Features
{
    /// <summary>Field operations, verification and e-signatures (every plan).</summary>
    public const string FieldOperations = "field-operations";

    /// <summary>Weighbridge tickets and basic sales (every plan).</summary>
    public const string Weighbridge = "weighbridge";

    /// <summary>Fuel management and anomaly detection (every plan).</summary>
    public const string Fuel = "fuel";

    /// <summary>Integrity dashboard (every plan).</summary>
    public const string IntegrityDashboard = "integrity-dashboard";

    /// <summary>Single store per site (Starter); see <see cref="InventoryMultiStore"/>.</summary>
    public const string Inventory = "inventory";

    /// <summary>Several stores, transfers and counts (Professional and up).</summary>
    public const string InventoryMultiStore = "inventory.multi-store";

    /// <summary>Maintenance and remaining useful life (Professional and up).</summary>
    public const string Maintenance = "maintenance";

    /// <summary>Plant graph, production and stockpiles (Business and up).</summary>
    public const string Plant = "plant";

    /// <summary>Asset finance, DSCR and weather advisory (Business and up).</summary>
    public const string Finance = "finance";

    /// <summary>AI copilot (Business and up; add-on on Professional).</summary>
    public const string AiCopilot = "ai.copilot";

    /// <summary>Edge agent for weighbridge auto-capture (Business and up; add-on on Starter and Professional).</summary>
    public const string EdgeAgent = "edge.agent";

    /// <summary>Telematics connectors (Enterprise; add-on on Business).</summary>
    public const string Telematics = "telematics";

    /// <summary>SSO by SAML / OIDC federation (Business and up).</summary>
    public const string Sso = "sso";

    /// <summary>Public API, read-only (Professional and up).</summary>
    public const string ApiRead = "api.read";

    /// <summary>Public API with writes and webhooks (Business and up).</summary>
    public const string ApiWrite = "api.write";

    /// <summary>Dedicated database, region or on-premises (Enterprise).</summary>
    public const string DedicatedHosting = "dedicated-hosting";

    public static IReadOnlyList<string> All { get; } =
    [
        FieldOperations, Weighbridge, Fuel, IntegrityDashboard, Inventory, InventoryMultiStore, Maintenance, Plant, Finance,
        AiCopilot, EdgeAgent, Telematics, Sso, ApiRead, ApiWrite, DedicatedHosting,
    ];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);
}

/// <summary>
/// Codes of the limits a plan sets (ADR-0023). A limit missing from a plan means unlimited. Usage metrics with the
/// same code are counted daily and checked when an item is created (soft limits with a 10 % grace margin).
/// </summary>
public static class Limits
{
    /// <summary>Active office users of the tenant (field users are unlimited on every plan).</summary>
    public const string OfficeUsers = "office-users";

    /// <summary>Assets included per site; the plan price is per site, so the tenant's limit is this × its sites.</summary>
    public const string AssetsPerSite = "assets-per-site";

    /// <summary>Edge agents included per site.</summary>
    public const string EdgeAgentsPerSite = "edge-agents-per-site";

    /// <summary>Years of data retention.</summary>
    public const string RetentionYears = "retention-years";

    public static IReadOnlyList<string> All { get; } = [OfficeUsers, AssetsPerSite, EdgeAgentsPerSite, RetentionYears];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);
}
