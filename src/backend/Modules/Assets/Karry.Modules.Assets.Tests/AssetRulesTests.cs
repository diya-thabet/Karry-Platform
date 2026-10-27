using Karry.Modules.Assets.Domain;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Assets.Tests;

/// <summary>Asset input rules, the type catalogue and the asset's own history (US-P1-AST-01, US-P1-AST-02, ADR-0026).</summary>
[Trait("Story", "US-P1-AST-02")]
public sealed class AssetRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);

    [Theory]
    [InlineData("pel-01", "PEL-01")]
    [InlineData(" CR/2.1 ", "CR/2.1")]
    public void Codes_are_trimmed_and_upper_cased(string input, string expected) => AssetRules.Code(input).Value.Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-PEL")]
    [InlineData("PEL 01")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
    public void Invalid_codes_are_refused(string? input) => AssetRules.Code(input).Error.Should().Be(AssetRules.CodeInvalid);

    [Fact]
    public void Details_are_trimmed_with_blanks_as_null_and_serials_and_plates_upper_cased()
    {
        var details = AssetRules.Details(" Pelle ", "excavator", "  ", " Cat   336 ", "ab-12", " 123  tu 4567 ", 2027, Today, Today).Value;

        details.Should().Be(new AssetDetails("Pelle", "excavator", null, "Cat 336", "AB-12", "123 TU 4567", 2027, Today));
    }

    [Fact]
    public void Details_out_of_bounds_are_refused_with_their_error()
    {
        Error(name: "").Should().Be("assets.name_invalid");
        Error(name: new string('n', 101)).Should().Be("assets.name_invalid");
        Error(type: "crane").Should().Be("assets.type_invalid");
        Error(make: new string('m', 61)).Should().Be("assets.text_too_long");
        Error(plate: new string('p', 21)).Should().Be("assets.text_too_long");
        Error(year: 1949).Should().Be("assets.year_invalid");
        Error(year: 2028).Should().Be("assets.year_invalid", "next year is the latest (a machine on order)");
        Error(acquiredOn: Today.AddDays(1)).Should().Be("assets.acquired_on_invalid");
        Error(acquiredOn: new DateOnly(1949, 12, 31)).Should().Be("assets.acquired_on_invalid");
        Error().Should().BeNull();
        Error(year: 1950, acquiredOn: new DateOnly(1950, 1, 1), name: new string('n', 100), plate: new string('p', 20)).Should().BeNull("the bounds themselves are accepted");
    }

    [Fact]
    public void Every_status_code_round_trips_and_others_are_refused()
    {
        foreach (var status in Enum.GetValues<AssetStatus>())
        {
            AssetRules.Status(AssetRules.StatusCode(status)).Value.Should().Be(status);
        }

        AssetRules.Status("Active").Error.Should().Be(AssetRules.StatusInvalid);
    }

    [Fact]
    public void The_catalogue_has_the_seeded_types_with_unique_codes()
    {
        AssetTypes.All.Select(t => t.Code).Should().OnlyHaveUniqueItems().And.HaveCount(17);
        AssetTypes.Find("light-vehicle")!.DefaultMeters.Should().Equal(MeterKind.Km);
        AssetTypes.Find("crusher-cone")!.DefaultMeters.Should().Equal(MeterKind.Hours);
        AssetTypes.Find("Excavator").Should().BeNull("codes are exact");
    }

    [Fact]
    public void An_asset_records_each_arrival_and_raises_one_event_per_real_change()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
        var (nord, sud) = (Guid.NewGuid(), Guid.NewGuid());
        var asset = Asset.Register(clock, new TenantId(Guid.NewGuid()), "PEL-01", Details(), nord, "admin");

        clock.Advance(TimeSpan.FromHours(1));
        asset.MoveTo(clock, sud, "admin").Value.Should().BeTrue();
        asset.MoveTo(clock, sud, "admin").Value.Should().BeFalse();
        asset.Change(clock, Details(), "admin").Value.Should().BeFalse();
        asset.SetStatus(clock, AssetStatus.Idle, "admin").Should().BeTrue();
        asset.SetStatus(clock, AssetStatus.Idle, "admin").Should().BeFalse();

        asset.Sites.Select(s => (s.SiteId, s.From)).Should().Equal((nord, clock.GetUtcNow().AddHours(-1)), (sud, clock.GetUtcNow()));
        asset.DomainEvents.Select(e => e.GetType()).Should().Equal(typeof(AssetRegistered), typeof(AssetMoved), typeof(AssetStatusChanged));
    }

    [Fact]
    public void A_disposed_asset_is_neither_changed_nor_moved()
    {
        var clock = new FakeTimeProvider();
        var asset = Asset.Register(clock, new TenantId(Guid.NewGuid()), "PEL-01", Details(), Guid.NewGuid(), null);
        asset.SetStatus(clock, AssetStatus.Disposed, null);

        asset.DisposedAt.Should().Be(clock.GetUtcNow());
        asset.Change(clock, Details() with { Name = "Other" }, null).Error.Should().Be(AssetRules.Disposed);
        asset.MoveTo(clock, Guid.NewGuid(), null).Error.Should().Be(AssetRules.Disposed);
    }

    private static AssetDetails Details() => new("Pelle", "excavator", "Caterpillar", "336", null, null, 2021, null);

    private static string? Error(
        string name = "Pelle", string type = "excavator", string? make = null, string? plate = null, int? year = null, DateOnly? acquiredOn = null)
    {
        var result = AssetRules.Details(name, type, make, null, null, plate, year, acquiredOn, Today);
        return result.IsSuccess ? null : result.Error.Code;
    }
}
