using Karry.Modules.Materials.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Materials.Tests;

/// <summary>Material input rules and density versions (US-P1-AST-04, ADR-0025).</summary>
[Trait("Story", "US-P1-AST-04")]
public sealed class MaterialRulesTests
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);
    private static readonly DateOnly Today = new(2026, 10, 26);

    [Theory]
    [InlineData("g-8/16", "G-8/16")]
    [InlineData(" CALC-0/31.5 ", "CALC-0/31.5")]
    [InlineData("A", "A")]
    public void Codes_are_trimmed_and_upper_cased(string input, string expected) => MaterialRules.Code(input).Value.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-G")]
    [InlineData("G 8/16")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    public void Invalid_codes_are_refused(string? input) => MaterialRules.Code(input).Error.Should().Be(MaterialRules.CodeInvalid);

    [Theory]
    [InlineData(null, 1.50, null, 0.02)]
    [InlineData(2.56, 1.60, 1.90, 0)]
    [InlineData(null, null, 3.2, 0.25)]
    [InlineData(1.2, null, null, 0.0001)]
    public void Densities_in_the_handbook_ranges_are_accepted(double? bank, double? loose, double? compacted, double moisture) =>
        MaterialRules.Version(Jan1, (decimal?)bank, (decimal?)loose, (decimal?)compacted, (decimal)moisture).IsSuccess.Should().BeTrue();

    [Fact]
    public void Densities_outside_the_ranges_are_refused_with_the_state()
    {
        MaterialRules.Version(Jan1, null, null, null, 0m).Error.Should().Be(MaterialRules.DensityRequired);
        Code(MaterialRules.Version(Jan1, 1.19m, null, null, 0m)).Should().Be(("materials.density_invalid", "bank"));
        Code(MaterialRules.Version(Jan1, null, 3.21m, null, 0m)).Should().Be(("materials.density_invalid", "loose"));
        Code(MaterialRules.Version(Jan1, null, null, 1.12345m, 0m)).Should().Be(("materials.density_invalid", "compacted"));
        MaterialRules.Version(Jan1, null, 1.5m, null, null).Error.Should().Be(MaterialRules.MoistureInvalid);
        MaterialRules.Version(Jan1, null, 1.5m, null, -0.01m).Error.Should().Be(MaterialRules.MoistureInvalid);
        MaterialRules.Version(Jan1, null, 1.5m, null, 0.2501m).Error.Should().Be(MaterialRules.MoistureInvalid);
    }

    [Fact]
    public void Bank_and_loose_densities_must_give_a_plausible_swell_factor()
    {
        MaterialRules.Version(Jan1, 2.0m, 1.9m, null, 0m).Error!.Code.Should().Be("materials.swell_invalid", "swell 0.0526 < 0.10");
        MaterialRules.Version(Jan1, 3.0m, 1.6m, null, 0m).Error!.Extensions["swell"].Should().Be("0.875");
        MaterialRules.Version(Jan1, 2.2m, 2.0m, null, 0m).IsSuccess.Should().BeTrue("swell 0.10 exactly");
        MaterialRules.Version(Jan1, 2.7m, 1.5m, null, 0m).IsSuccess.Should().BeTrue("swell 0.80 exactly");
    }

    [Fact]
    public void Equal_densities_written_differently_are_one_version()
    {
        var a = MaterialRules.Version(Jan1, null, 1.50m, null, 0.020m).Value;
        var b = MaterialRules.Version(Jan1, null, 1.5m, null, 0.02m).Value;

        a.Should().Be(b);
        a.Loose!.Value.Scale.Should().Be(1, "stored without trailing zeros");
    }

    [Fact]
    public void The_version_valid_on_a_date_is_the_last_one_started()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));
        var material = Material.Create(clock, new TenantId(Guid.NewGuid()), "G-8/16", "Gravier 8/16", MaterialKind.Product, Version(Jan1, 1.50m), "admin");
        material.SetVersion(clock, Version(Today.AddDays(10), 1.55m), Today, "admin").Value.Should().BeTrue();

        material.VersionOn(Jan1.AddDays(-1)).Should().BeNull();
        material.VersionOn(Jan1)!.Loose.Should().Be(1.50m);
        material.VersionOn(Today.AddDays(9))!.Loose.Should().Be(1.50m);
        material.VersionOn(Today.AddDays(10))!.Loose.Should().Be(1.55m);
        material.VersionOn(new DateOnly(2099, 1, 1))!.Loose.Should().Be(1.55m);
    }

    [Fact]
    public void Only_future_versions_change_and_one_always_remains()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));
        var material = Material.Create(clock, new TenantId(Guid.NewGuid()), "G-8/16", "Gravier 8/16", MaterialKind.Product, Version(Jan1, 1.50m), "admin");

        material.SetVersion(clock, Version(Today, 1.6m), Today, "admin").Error!.Code.Should().Be("materials.date_in_past");
        material.SetVersion(clock, Version(Jan1, 1.6m), Today, "admin").Error!.Extensions["today"].Should().Be("2026-10-26");
        material.RemoveVersion(clock, Jan1, Today, "admin").Error!.Code.Should().Be("materials.date_in_past");
        material.RemoveVersion(clock, Today.AddDays(3), Today, "admin").Error.Should().Be(MaterialRules.VersionNotFound);

        material.SetVersion(clock, Version(Today.AddDays(1), 1.6m), Today, "admin").Value.Should().BeTrue();
        material.SetVersion(clock, Version(Today.AddDays(1), 1.60m), Today, "admin").Value.Should().BeFalse("unchanged: no ledger entry");
        material.SetVersion(clock, Version(Today.AddDays(1), 1.7m), Today, "admin").Value.Should().BeTrue("a future version is replaced");
        material.Versions.Should().HaveCount(2);
        material.RemoveVersion(clock, Today.AddDays(1), Today, "admin").Value.Should().BeTrue();
        material.Versions.Should().ContainSingle();

        var future = Material.Create(clock, new TenantId(Guid.NewGuid()), "NEW", "Nouveau", MaterialKind.Product, Version(Today.AddDays(5), 1.5m), "admin");
        future.RemoveVersion(clock, Today.AddDays(5), Today, "admin").Error.Should().Be(MaterialRules.LastVersion);
    }

    [Fact]
    public void An_archived_material_keeps_its_densities()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));
        var material = Material.Create(clock, new TenantId(Guid.NewGuid()), "G-8/16", "Gravier 8/16", MaterialKind.Product, Version(Jan1, 1.50m), "admin");
        material.Archive(clock, "admin").Should().BeTrue();

        material.SetVersion(clock, Version(Today.AddDays(1), 1.6m), Today, "admin").Error.Should().Be(MaterialRules.Archived);
        material.VersionOn(Today).Should().NotBeNull("past quantities still convert");
        material.Restore(clock, "admin").Should().BeTrue();
        material.Archive(clock, "admin").Should().BeTrue();
        material.Archive(clock, "admin").Should().BeFalse();
    }

    private static DensityVersion Version(DateOnly from, decimal loose) => MaterialRules.Version(from, null, loose, null, 0.02m).Value;

    private static (string, object?) Code(Karry.SharedKernel.Results.Result<DensityVersion> result) =>
        (result.Error!.Code, result.Error.Extensions.GetValueOrDefault("state"));
}
