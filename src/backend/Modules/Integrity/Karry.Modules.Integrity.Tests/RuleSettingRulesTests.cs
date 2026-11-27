using Karry.Modules.Integrity.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Integrity.Tests;

/// <summary>
/// What a company may set on a rule (ADR-0036 §1, US-P2-INT-01 AC3): security rules stay on, thresholds stay within
/// their range and decimals, a threshold not named keeps its value, and only a real change is ledgered.
/// </summary>
[Trait("Story", "US-P2-INT-01")]
public sealed class RuleSettingRulesTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;
    private static readonly TenantId Company = new(Guid.CreateVersion7());

    private static RuleDefinition Rule(string code) => RuleCatalog.Find(code)!;

    private static Dictionary<string, decimal> Set(string name, decimal value) => new(StringComparer.Ordinal) { [name] = value };

    [Theory]
    [InlineData("RULE-FO-05")]
    [InlineData("RULE-FO-06")]
    [InlineData("RULE-FO-07")]
    [InlineData("RULE-FO-09")]
    [InlineData("RULE-FO-10")]
    [InlineData("RULE-FO-11")]
    public void A_security_rule_cannot_be_switched_off_and_always_runs(string code)
    {
        RuleSetting.Create(Clock, Company, Rule(code), enabled: false, Set("x", 1), "admin").Error.Should().Be(RuleSettingRules.Locked);
        RuleSettingRules.Check(Rule(code), enabled: false, new Dictionary<string, decimal>()).Should().Be(RuleSettingRules.Locked);
        RuleSettingRules.Runs(Rule(code), null).Should().BeTrue();
    }

    [Fact]
    public void Every_rule_of_the_catalogue_but_the_security_ones_can_be_switched_off()
    {
        RuleCatalog.All.Where(r => !r.Security).Select(r => r.Code).Should().Equal("RULE-FO-01", "RULE-FO-02", "RULE-FO-03", "RULE-FO-04", "RULE-FO-08", "RULE-WB-09");
        var off = RuleSetting.Create(Clock, Company, Rule("RULE-FO-01"), enabled: false, new Dictionary<string, decimal>(), "admin").Value;
        RuleSettingRules.Runs(Rule("RULE-FO-01"), off).Should().BeFalse();
    }

    [Fact]
    public void A_threshold_is_refused_outside_its_range_with_too_many_decimals_or_when_the_rule_has_none_of_that_name()
    {
        var margin = Rule("RULE-FO-04");
        RuleSettingRules.Check(margin, true, Set("marginMetres", -1))?.Code.Should().Be("integrity.threshold_invalid");
        RuleSettingRules.Check(margin, true, Set("marginMetres", 5001)).Should().NotBeNull();
        RuleSettingRules.Check(margin, true, Set("marginMetres", 250.5m)).Should().NotBeNull("metres are whole");
        RuleSettingRules.Check(margin, true, Set("toleranceHours", 1))?.Code.Should().Be("integrity.threshold_unknown");
        RuleSettingRules.Check(Rule("RULE-FO-02"), true, Set("toleranceHours", 0.75m)).Should().NotBeNull("tenths of an hour, as the meters count");
        RuleSettingRules.Check(Rule("RULE-FO-02"), true, Set("toleranceHours", 1.5m)).Should().BeNull();
        RuleSettingRules.Check(margin, true, Set("marginMetres", 0)).Should().BeNull("the boundary itself");
    }

    [Fact]
    public void Every_tenth_of_an_hour_within_the_range_is_accepted_and_kept_exactly() =>
        Enumerable.Range(0, 41).Select(tenths => tenths / 10m).Should().OnlyContain(hours =>
            RuleSetting.Create(Clock, Company, Rule("RULE-FO-02"), true, Set("toleranceHours", hours), "admin").Value.Thresholds["toleranceHours"] == hours);

    [Fact]
    public void A_new_setting_holds_every_threshold_and_a_change_keeps_the_ones_not_named()
    {
        var setting = RuleSetting.Create(Clock, Company, Rule("RULE-FO-03"), true, new Dictionary<string, decimal>(), "admin").Value;
        setting.Thresholds.Should().Equal(new Dictionary<string, decimal> { ["maxSkewMinutes"] = 10m });

        setting.Change(Clock, Rule("RULE-FO-03"), true, Set("maxSkewMinutes", 30), "admin").Value.Should().BeTrue();
        setting.Change(Clock, Rule("RULE-FO-03"), false, new Dictionary<string, decimal>(), "admin").Value.Should().BeTrue();

        (setting.Enabled, setting.Thresholds["maxSkewMinutes"]).Should().Be((false, 30m));
    }

    [Fact]
    public void Only_a_real_change_is_ledgered_under_the_person_who_made_it()
    {
        var setting = RuleSetting.Create(Clock, Company, Rule("RULE-FO-04"), true, Set("marginMetres", 300), "admin-1").Value;
        setting.Change(Clock, Rule("RULE-FO-04"), true, Set("marginMetres", 300), "admin-2").Value.Should().BeFalse();
        setting.Change(Clock, Rule("RULE-FO-04"), true, Set("marginMetres", 350), "admin-2").Value.Should().BeTrue();

        setting.DomainEvents.OfType<RuleSettingsChanged>().Select(e => (e.Actor, e.Thresholds["marginMetres"]))
            .Should().Equal(("admin-1", 300m), ("admin-2", 350m));
        setting.ChangedBy.Should().Be("admin-2");
    }

    [Fact]
    public void A_refused_change_leaves_the_setting_as_it_was()
    {
        var setting = RuleSetting.Create(Clock, Company, Rule("RULE-FO-04"), true, Set("marginMetres", 300), "admin").Value;
        setting.Change(Clock, Rule("RULE-FO-04"), false, Set("marginMetres", 9999), "admin").IsSuccess.Should().BeFalse();
        (setting.Enabled, setting.Thresholds["marginMetres"]).Should().Be((true, 300m));
    }

    [Fact]
    public void Without_settings_a_rule_runs_with_the_catalogue_defaults() =>
        RuleSettingRules.Effective(Rule("RULE-FO-02"), null).Should().Equal(new Dictionary<string, decimal> { ["toleranceHours"] = 0.5m });
}
