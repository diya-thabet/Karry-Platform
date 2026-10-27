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

/// <summary>
/// Add-ons a subscription can carry, with what each one grants (business model §2.3 add-ons). Their prices are on
/// the manual invoices until the billing provider (Phase 10).
/// </summary>
/// <summary>
/// What usage meters count (US-P1-BIL-04): each module that owns a counted thing reports it (<c>IUsageSource</c>), a
/// daily snapshot keeps one value per tenant, metric and day. Field users are never counted (unlimited on every plan).
/// </summary>
public static class Metrics
{
    /// <summary>Office users who are invited or active (an open invitation holds a seat); not field users.</summary>
    public const string OfficeUsers = "office-users";

    public const string Sites = "sites";

    public const string Assets = "assets";

    public const string StorageBytes = "storage-bytes";

    public static IReadOnlyList<string> All { get; } = [OfficeUsers, Sites, Assets, StorageBytes];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);

    /// <summary>The plan limit a metric is held against (its tenant-wide total), or null when nothing limits it.</summary>
    public static string? LimitOf(string metric) => metric switch
    {
        OfficeUsers => Limits.OfficeUsers,
        Assets => Limits.AssetsPerSite,
        _ => null,
    };
}

/// <summary>
/// Soft limits (business model §2.4, ADR-0023): up to the limit nothing shows; above it the admin sees a banner and
/// Karry is told, still nothing is blocked; at the ceiling (the limit plus a 10 % grace, at least one) only creating
/// another counted item is refused. Field capture never is.
/// </summary>
public static class SoftLimit
{
    public const string Within = "within";

    public const string Over = "over";

    public const string Blocked = "blocked";

    /// <summary>The limit plus 10 % rounded up: 20 → 22, 15 → 17, 3 → 4, 0 → 0.</summary>
    public static long Ceiling(long limit) => limit + ((limit + 9) / 10);

    /// <summary><see cref="Within"/>, <see cref="Over"/> (banner) or <see cref="Blocked"/> (no more creation); unlimited is always within.</summary>
    public static string State(long usage, long? limit) => limit switch
    {
        null => Within,
        { } l when usage >= Ceiling(l) => Blocked,
        { } l when usage > l => Over,
        _ => Within,
    };

    /// <summary>Whether one more item may be created at <paramref name="usage"/>.</summary>
    public static bool AllowsOneMore(long usage, long? limit) => Allows(usage, limit, 1);

    /// <summary>Whether <paramref name="count"/> more items may be created at <paramref name="usage"/> (all of them within the grace).</summary>
    public static bool Allows(long usage, long? limit, int count) => limit is not { } l || usage + count <= Ceiling(l);
}

public static class AddOns
{
    /// <summary>AI copilot on a plan that does not include it (Professional).</summary>
    public const string AiCopilot = "ai-copilot";

    /// <summary>Edge agent licence: one more edge agent per site per unit.</summary>
    public const string EdgeAgent = "edge-agent";

    /// <summary>Telematics connector (Business).</summary>
    public const string Telematics = "telematics";

    public static IReadOnlyList<string> All { get; } = [AiCopilot, EdgeAgent, Telematics];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);

    /// <summary>The features an add-on grants.</summary>
    public static IReadOnlyList<string> FeaturesOf(string addOn) => addOn switch
    {
        AiCopilot => [Features.AiCopilot],
        EdgeAgent => [Features.EdgeAgent],
        Telematics => [Features.Telematics],
        _ => [],
    };

    /// <summary>How much an add-on raises a limit per unit (only the edge agent does: one per site per unit).</summary>
    public static int LimitIncrease(string addOn, string limit) =>
        addOn == EdgeAgent && limit == Limits.EdgeAgentsPerSite ? 1 : 0;
}

/// <summary>
/// What a tenant may use right now (ADR-0023): plan version + add-ons + overrides + subscription state, resolved
/// by the Billing module. <c>Limits</c>: a limit not present is unlimited; per-site limits are per site, the tenant's
/// total is the value × <c>Sites</c> (plus <c>ExtraAssets</c> for assets).
/// </summary>
public sealed record TenantEntitlements(
    string PlanCode,
    int PlanVersion,
    string Status,
    DateTimeOffset? TrialEndsAt,
    IReadOnlySet<string> Features,
    IReadOnlyDictionary<string, int> Limits,
    int Sites,
    int ExtraAssets)
{
    public bool Includes(string feature) => Features.Contains(feature);

    /// <summary>
    /// The limit a usage metric is held against: the sites bought for <see cref="Metrics.Sites"/> (ADR-0024), else the
    /// tenant-wide total of its plan limit (<see cref="Metrics.LimitOf"/>); null when nothing limits it.
    /// </summary>
    public long? MetricLimit(string metric) => metric == Metrics.Sites
        ? Sites
        : Metrics.LimitOf(metric) is { } limit ? TotalLimit(limit) : null;

    /// <summary>The tenant-wide limit of <paramref name="limit"/>, or null when unlimited.</summary>
    public long? TotalLimit(string limit) => Limits.TryGetValue(limit, out var value)
        ? limit switch
        {
            Entitlements.Limits.AssetsPerSite => ((long)value * Sites) + ExtraAssets,
            Entitlements.Limits.EdgeAgentsPerSite => (long)value * Sites,
            _ => value,
        }
        : null;
}
