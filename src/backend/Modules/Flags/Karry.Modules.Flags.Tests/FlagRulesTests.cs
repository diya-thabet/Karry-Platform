using Karry.Modules.Flags.Application;
using Karry.Modules.Flags.Domain;
using Karry.SharedKernel.Flags;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Flags.Tests;

/// <summary>Flag evaluation, most specific first (US-P1-PLT-05 AC1), and what a change records.</summary>
[Trait("Story", "US-P1-PLT-05")]
public sealed class FlagRulesTests
{
    private static readonly FlagDefinition Flag = FeatureFlags.PortalUsageBanner;
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(null, null, null, null, true, FlagSource.Default)]
    [InlineData(false, null, null, null, false, FlagSource.Environment)]
    [InlineData(false, true, null, null, true, FlagSource.Platform)]
    [InlineData(true, false, true, null, true, FlagSource.Tenant)]
    [InlineData(true, true, true, false, false, FlagSource.User)]
    [InlineData(null, false, null, true, true, FlagSource.User)]
    public void The_most_specific_value_wins(bool? environment, bool? platform, bool? tenant, bool? user, bool expected, FlagSource source) =>
        FlagRules.Evaluate(Flag, environment, platform, tenant, user).Should().Be((expected, source));

    [Fact]
    public void Only_declared_flags_and_sane_subjects_are_accepted()
    {
        FlagRules.Validate("portal.usage-baner", null)!.Code.Should().Be("flags.flag_unknown");
        FlagRules.Validate(Flag.Key, new string('x', 256))!.Code.Should().Be("flags.subject_invalid");
        FlagRules.Validate(Flag.Key, "a\nb")!.Code.Should().Be("flags.subject_invalid");
        FlagRules.Validate(Flag.Key, "keycloak-user-of-a").Should().BeNull();
    }

    [Fact]
    public void A_company_change_records_before_and_after_once()
    {
        var flag = TenantFlag.Create(clock, new TenantId(Guid.CreateVersion7()), Flag.Key, null, false, "op-1").Value;

        flag.Change(clock, false, "op-1").Should().BeFalse("the same value changes nothing");
        flag.Change(clock, null, "op-2").Should().BeTrue();

        flag.DomainEvents.Cast<TenantFlagChanged>().Select(e => (e.Before, e.After, e.Actor))
            .Should().Equal((null, false, "op-1"), (false, null, "op-2"));
    }

    [Fact]
    public void A_platform_change_is_one_history_entry_per_change()
    {
        var flag = PlatformFlag.For(Flag.Key);

        var first = flag.Change(clock, false, "op-1");
        flag.Change(clock, false, "op-1").Should().BeNull();
        var second = flag.Change(clock, true, "op-2");

        (first!.Before, first.After).Should().Be((null, false));
        (second!.Before, second.After, second.Actor).Should().Be((false, true, "op-2"));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("maybe", null)]
    [InlineData(null, null)]
    public void The_environment_value_comes_from_configuration(string? configured, bool? expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Flags:Environment:{Flag.Key}"] = configured }).Build();
        var operations = new FlagOperations(null!, null!, clock, configuration);

        operations.EnvironmentValue(Flag).Should().Be(expected);
    }
}
