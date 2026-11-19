using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Integrity.Domain;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Integrity.Application;

/// <summary>The current company's rule settings (RLS).</summary>
public interface IRuleSettingStore
{
    Task<IReadOnlyList<RuleSetting>> ListAsync(CancellationToken cancellationToken);

    Task<RuleSetting?> FindAsync(string rule, CancellationToken cancellationToken);

    /// <summary>False when someone else saved the rule's settings first (a new row or a newer version): nothing is saved.</summary>
    Task<bool> TrySaveAsync(RuleSetting setting, CancellationToken cancellationToken);
}

/// <summary>
/// A rule as the company runs it: the catalogue's definition and the company's settings, if any (none: the defaults,
/// version 0).
/// </summary>
public sealed record EffectiveRule(RuleDefinition Definition, RuleSetting? Setting)
{
    public bool Enabled => RuleSettingRules.Runs(Definition, Setting);

    public IReadOnlyDictionary<string, decimal> Thresholds => RuleSettingRules.Effective(Definition, Setting?.Thresholds);

    /// <summary>The settings' version for <c>If-Match</c>: 0 while the company runs the defaults.</summary>
    public uint Version => Setting?.Version ?? 0;
}

/// <summary>What a person sets on a rule: switched on, and the thresholds to change (one not named keeps its value).</summary>
public sealed record RuleSettingsInput(bool Enabled, IReadOnlyDictionary<string, decimal>? Thresholds);

/// <summary>
/// The company's rule settings (ADR-0036 §1, US-P2-INT-01 AC3): read by everyone who reads flags, changed by a company
/// administrator; a security rule cannot be switched off; every change is ledgered under the person's name.
/// </summary>
public sealed class RuleSettingOperations(IRuleSettingStore store, ITenantContext tenant, ICallerAccess caller, TimeProvider clock)
{
    public async Task<IReadOnlyList<EffectiveRule>> ListAsync(CancellationToken cancellationToken)
    {
        var settings = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. RuleCatalog.All.Select(r => new EffectiveRule(r, settings.SingleOrDefault(s => s.Rule == r.Code)))];
    }

    /// <summary>
    /// Sets a rule. With <paramref name="expectedVersion"/> (the form's <c>If-Match</c>; 0: the defaults it was filled
    /// from), only that version is changed; otherwise <see cref="RecordVersions.Conflict"/> and nothing is saved (H-C1).
    /// </summary>
    public async Task<Result<EffectiveRule>> SetAsync(string rule, RuleSettingsInput input, uint? expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (RuleCatalog.Find(rule) is not { } definition)
        {
            return RuleSettingRules.UnknownRule;
        }

        var setting = await store.FindAsync(rule, cancellationToken).ConfigureAwait(false);
        if (!RecordVersions.Matches(setting?.Version ?? 0, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        var thresholds = input.Thresholds ?? new Dictionary<string, decimal>();
        if (setting is null)
        {
            var company = tenant.Current ?? throw new InvalidOperationException("Rule settings belong to a tenant.");
            var created = RuleSetting.Create(clock, company, definition, input.Enabled, thresholds, caller.Subject);
            if (!created.IsSuccess)
            {
                return created.Error;
            }

            setting = created.Value;
        }
        else
        {
            var changed = setting.Change(clock, definition, input.Enabled, thresholds, caller.Subject);
            if (!changed.IsSuccess)
            {
                return changed.Error;
            }

            if (!changed.Value)
            {
                return new EffectiveRule(definition, setting);
            }
        }

        return await store.TrySaveAsync(setting, cancellationToken).ConfigureAwait(false)
            ? new EffectiveRule(definition, setting)
            : RecordVersions.Conflict;
    }
}
