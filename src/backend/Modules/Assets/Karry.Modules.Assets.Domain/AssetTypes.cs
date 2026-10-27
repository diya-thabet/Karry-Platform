namespace Karry.Modules.Assets.Domain;

/// <summary>What a meter counts (US-P1-AST-03 AC1): engine hours or distance.</summary>
public enum MeterKind
{
    Hours = 1,
    Km = 2,
}

/// <summary>
/// A machine type (US-P1-AST-01): the meters a new asset of the type gets and the component templates maintenance uses
/// (Phase 4). Codes are stable: the apps translate the names.
/// </summary>
public sealed record AssetType(string Code, IReadOnlyList<MeterKind> DefaultMeters, IReadOnlyList<string> Components);

/// <summary>
/// The seeded type catalogue (US-P1-AST-01 AC1–AC2, ADR-0026): the same for every company, in code. Default meters and
/// components are drafts until the owner reviews them (D-03).
/// </summary>
public static class AssetTypes
{
    private static readonly MeterKind[] Hours = [MeterKind.Hours];
    private static readonly MeterKind[] Km = [MeterKind.Km];
    private static readonly MeterKind[] HoursAndKm = [MeterKind.Hours, MeterKind.Km];

    public static IReadOnlyList<AssetType> All { get; } =
    [
        new("excavator", Hours, ["engine", "hydraulic-pump", "undercarriage", "slew-ring", "final-drive", "bucket"]),
        new("wheel-loader", Hours, ["engine", "transmission", "hydraulic-pump", "axles", "tyres", "bucket"]),
        new("haul-truck", HoursAndKm, ["engine", "transmission", "suspension", "brakes", "tyres", "body"]),
        new("dozer", Hours, ["engine", "transmission", "undercarriage", "final-drive", "blade"]),
        new("grader", Hours, ["engine", "transmission", "circle", "tyres", "blade"]),
        new("drill-rig", Hours, ["engine", "compressor", "hammer", "drill-rods", "undercarriage"]),
        new("crusher-jaw", Hours, ["motor", "v-belts", "flywheel", "bearings", "jaw-plates"]),
        new("crusher-cone", Hours, ["motor", "v-belts", "lubrication", "bearings", "mantle", "concave"]),
        new("crusher-impact", Hours, ["motor", "rotor", "bearings", "blow-bars", "impact-plates"]),
        new("crusher-vsi", Hours, ["motor", "rotor", "bearings", "rotor-tips", "anvils"]),
        new("screen", Hours, ["motor", "bearings", "springs", "screen-media"]),
        new("washer", Hours, ["motor", "bearings", "screw", "liners"]),
        new("conveyor", Hours, ["motor", "gearbox", "drive-pulley", "idlers", "belt"]),
        new("generator", Hours, ["engine", "alternator"]),
        new("fuel-bowser", Km, ["engine", "tyres", "pump", "flowmeter"]),
        new("light-vehicle", Km, ["engine", "brakes", "tyres"]),
        new("other", [], []),
    ];

    public static AssetType? Find(string? code) => All.SingleOrDefault(t => string.Equals(t.Code, code, StringComparison.Ordinal));
}
