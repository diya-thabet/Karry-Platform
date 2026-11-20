using System.Globalization;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Integrity.Domain;

/// <summary>
/// A rule's threshold: its default (detection-rules.md), the range a company may set it to, how many decimals it takes
/// and its unit (explicit units: <c>h</c>, <c>min</c>, <c>m</c>).
/// </summary>
public sealed record ThresholdDefinition(string Name, decimal Default, decimal Min, decimal Max, int Decimals, string Unit)
{
    /// <summary>Within the range, with no more decimals than the threshold takes.</summary>
    public bool Accepts(decimal value) => value >= Min && value <= Max && decimal.Round(value, Decimals) == value;
}

/// <summary>A rule as the catalogue defines it (detection-rules.md): severity, whether it is a security rule, thresholds.</summary>
public sealed record RuleDefinition(string Code, string Severity, bool Security, IReadOnlyList<ThresholdDefinition> Thresholds)
{
    /// <summary>The catalogue's value of every threshold.</summary>
    public IReadOnlyDictionary<string, decimal> Defaults => Thresholds.ToDictionary(t => t.Name, t => t.Default, StringComparer.Ordinal);
}

/// <summary>The rules of P2.7 with their default settings (ADR-0036 §1); security rules cannot be switched off.</summary>
public static class RuleCatalog
{
    public static IReadOnlyList<RuleDefinition> All { get; } =
    [
        new("RULE-FO-01", "high", false, []),
        new("RULE-FO-02", "high", false, [new("toleranceHours", 0.5m, 0m, 4m, 1, "h")]),
        new("RULE-FO-03", "medium", false, [new("maxSkewMinutes", 10m, 1m, 1440m, 0, "min")]),
        new("RULE-FO-04", "medium", false, [new("marginMetres", 200m, 0m, 5000m, 0, "m")]),
        new("RULE-FO-05", "high", true, []),
        new("RULE-FO-06", "high", true, []),
        new("RULE-FO-07", "critical", true, []),
        new("RULE-FO-09", "critical", true, []),
        new("RULE-FO-10", "high", true, []),
    ];

    public static RuleDefinition? Find(string rule) => All.SingleOrDefault(r => string.Equals(r.Code, rule, StringComparison.Ordinal));
}

public readonly record struct RuleSettingId(Guid Value) : IEntityId<RuleSettingId>
{
    public static RuleSettingId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

[EventType("integrity.rule_settings_changed.v1")]
[AuditSubject("integrity_rule_setting", nameof(SettingId))]
public sealed record RuleSettingsChanged(Guid SettingId, string Rule, bool Enabled, IReadOnlyDictionary<string, decimal> Thresholds, string? Actor) : DomainEvent;

/// <summary>
/// A company's settings of one rule (ADR-0036 §1): switched on or off and its thresholds; a rule without a row runs
/// with the catalogue's defaults. Every change is ledgered under the person who made it. A security rule cannot be
/// switched off (RULE-FO-05, 06, 07).
/// </summary>
public sealed class RuleSetting : AggregateRoot<RuleSettingId>, ITenantOwned
{
    private RuleSetting(RuleSettingId id)
        : base(id)
    {
    }

    // EF Core
    private RuleSetting()
        : base(default)
    {
    }

    public TenantId TenantId { get; private set; }

    public string Rule { get; private set; } = null!;

    public bool Enabled { get; private set; }

    /// <summary>Every threshold of the rule, as last set (<see cref="RuleSettingRules.Effective"/> fills one the catalogue added since).</summary>
    public IReadOnlyDictionary<string, decimal> Thresholds { get; private set; } = new Dictionary<string, decimal>();

    public DateTimeOffset ChangedAt { get; private set; }

    public string? ChangedBy { get; private set; }

    /// <summary>The row version (<c>xmin</c>, H-C1).</summary>
    public uint Version { get; private set; }

    public static Result<RuleSetting> Create(
        TimeProvider clock, TenantId tenant, RuleDefinition rule, bool enabled, IReadOnlyDictionary<string, decimal> thresholds, string? actor)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var setting = new RuleSetting(EntityId.New<RuleSettingId>(clock)) { TenantId = tenant, Rule = rule.Code };
        var changed = setting.Apply(clock, rule, enabled, thresholds, actor, always: true);
        return changed.IsSuccess ? setting : changed.Error;
    }

    /// <summary>
    /// Applies the new settings; a threshold not given keeps its value. False when nothing changes (nothing is ledgered).
    /// </summary>
    public Result<bool> Change(TimeProvider clock, RuleDefinition rule, bool enabled, IReadOnlyDictionary<string, decimal> thresholds, string? actor) =>
        Apply(clock, rule, enabled, thresholds, actor, always: false);

    private Result<bool> Apply(TimeProvider clock, RuleDefinition rule, bool enabled, IReadOnlyDictionary<string, decimal> thresholds, string? actor, bool always)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (RuleSettingRules.Check(rule, enabled, thresholds) is { } invalid)
        {
            return invalid;
        }

        var current = RuleSettingRules.Effective(rule, Thresholds);
        var values = RuleSettingRules.Effective(rule, current.ToDictionary(c => c.Key, c => thresholds.TryGetValue(c.Key, out var v) ? v : c.Value, StringComparer.Ordinal));
        if (!always && Enabled == enabled && values.Count == Thresholds.Count && values.All(v => Thresholds.TryGetValue(v.Key, out var x) && x == v.Value))
        {
            return false;
        }

        var at = clock.GetUtcNow();
        (Enabled, Thresholds, ChangedAt, ChangedBy) = (enabled, values, at, actor);
        Raise(new RuleSettingsChanged(Id.Value, Rule, enabled, values, actor) { OccurredAt = at });
        return true;
    }
}

/// <summary>What a company may set (ADR-0036 §1), shared by the settings and their reading.</summary>
public static class RuleSettingRules
{
    public static readonly Error UnknownRule = Error.NotFound("integrity.rule_not_found", "No such rule.");

    public static readonly Error Locked = Error.Validation("integrity.rule_locked", "A security rule cannot be switched off.");

    public static Error UnknownThreshold(string name) => Error.Validation("integrity.threshold_unknown", $"The rule has no threshold {name}.");

    public static Error OutOfRange(ThresholdDefinition threshold)
    {
        ArgumentNullException.ThrowIfNull(threshold);
        return Error.Validation(
            "integrity.threshold_invalid",
            string.Create(CultureInfo.InvariantCulture, $"{threshold.Name} is {threshold.Min} to {threshold.Max} {threshold.Unit}, with at most {threshold.Decimals} decimals."));
    }

    /// <summary>The first thing wrong with the settings, or null.</summary>
    public static Error? Check(RuleDefinition rule, bool enabled, IReadOnlyDictionary<string, decimal> thresholds)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (rule.Security && !enabled)
        {
            return Locked;
        }

        foreach (var (name, value) in thresholds)
        {
            var threshold = rule.Thresholds.SingleOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
            if (threshold is null)
            {
                return UnknownThreshold(name);
            }

            if (!threshold.Accepts(value))
            {
                return OutOfRange(threshold);
            }
        }

        return null;
    }

    /// <summary>
    /// Every threshold of the rule: the value given, else the catalogue's default; a name the catalogue does not have is
    /// dropped.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> Effective(RuleDefinition rule, IReadOnlyDictionary<string, decimal>? thresholds)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Thresholds.ToDictionary(
            t => t.Name, t => thresholds is not null && thresholds.TryGetValue(t.Name, out var value) ? value : t.Default, StringComparer.Ordinal);
    }

    /// <summary>Whether the rule runs: a security rule always does, whatever is stored; without settings, every rule does.</summary>
    public static bool Runs(RuleDefinition rule, RuleSetting? setting)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Security || setting is null || setting.Enabled;
    }
}
