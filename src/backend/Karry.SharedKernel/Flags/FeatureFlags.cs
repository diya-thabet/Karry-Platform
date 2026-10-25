namespace Karry.SharedKernel.Flags;

/// <summary>A feature flag: rollout of code, never plan gating (that is entitlements, ADR-0019 / ADR-0023).</summary>
public sealed record FlagDefinition(string Key, string Description, bool Default);

/// <summary>
/// Every flag the code reads (US-P1-PLT-05). Declared here like permissions and feature codes: changing a value needs no
/// release, adding or removing a flag does. A key not declared is refused when set and evaluates to the caller's default.
/// Remove a flag once its rollout is finished.
/// </summary>
public static class FeatureFlags
{
    /// <summary>The portal's over-limit banner (US-P1-BIL-04): a kill switch should it mislead customers.</summary>
    public static readonly FlagDefinition PortalUsageBanner = new("portal.usage-banner", "Show the over-limit banner in the portal.", Default: true);

    public static IReadOnlyList<FlagDefinition> All { get; } = [PortalUsageBanner];

    public static FlagDefinition? Find(string? key) => All.FirstOrDefault(f => f.Key == key);
}
