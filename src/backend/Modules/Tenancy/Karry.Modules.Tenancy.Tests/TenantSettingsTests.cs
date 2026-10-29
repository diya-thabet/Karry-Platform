using Karry.Modules.Tenancy.Domain;

namespace Karry.Modules.Tenancy.Tests;

/// <summary>Company settings rules (US-P1-TEN-04 AC1): each value is checked before anything is stored.</summary>
[Trait("Story", "US-P1-TEN-04")]
public sealed class TenantSettingsTests
{
    [Fact]
    public void Valid_settings_are_normalised()
    {
        var settings = TenantSettings.Create("  Carrières   du Nord  SA ", "#1f4e79", "en", "TND", "imperial", "Africa/Tunis", 7).Value;

        settings.Should().Be(new TenantSettings("Carrières du Nord SA", "#1F4E79", "en", "TND", UnitSystem.Imperial, "Africa/Tunis", 7));
        TenantSettings.Create("Carrières du Nord SA", "  ", "fr", "EUR", "Metric", "UTC", 1).Value.PrimaryColour.Should().BeNull("no colour means Karry's own");
    }

    [Theory]
    [InlineData("ar")]
    [InlineData("emk")]
    [InlineData("nqo")]
    public void A_company_can_default_to_Arabic_or_Malinke_in_either_script(string language) =>
        TenantSettings.Create("Carrière", null, language, "XOF", "metric", "Africa/Conakry", 1).Value.Language.Should().Be(language);

    [Theory]
    [InlineData("x", "#1F4E79", "fr", "EUR", "metric", "UTC", 1, "tenant.legal_name_invalid")]
    [InlineData("Carrière", "1F4E79", "fr", "EUR", "metric", "UTC", 1, "tenant.colour_invalid")]
    [InlineData("Carrière", "#1F4E7G", "fr", "EUR", "metric", "UTC", 1, "tenant.colour_invalid")]
    [InlineData("Carrière", "#FFD700", "fr", "EUR", "metric", "UTC", 1, "tenant.colour_contrast")]
    [InlineData("Carrière", "#777777", "fr", "EUR", "metric", "UTC", 1, "tenant.colour_contrast")]
    [InlineData("Carrière", null, "de", "EUR", "metric", "UTC", 1, "tenant.language_unsupported")]
    [InlineData("Carrière", null, "AR", "EUR", "metric", "UTC", 1, "tenant.language_unsupported")]
    [InlineData("Carrière", null, "fr", "EUO", "metric", "UTC", 1, "tenant.currency_unsupported")]
    [InlineData("Carrière", null, "fr", "EUR", "imperial-ish", "UTC", 1, "tenant.unit_system_invalid")]
    [InlineData("Carrière", null, "fr", "EUR", "1", "UTC", 1, "tenant.unit_system_invalid")]
    [InlineData("Carrière", null, "fr", "EUR", "metric", "Mars/Olympus", 1, "tenant.time_zone_unknown")]
    [InlineData("Carrière", null, "fr", "EUR", "metric", "UTC", 13, "tenant.fiscal_month_invalid")]
    [InlineData("Carrière", null, "fr", "EUR", "metric", "UTC", 0, "tenant.fiscal_month_invalid")]
    public void Invalid_values_are_refused_with_their_own_code(
        string legalName, string? colour, string language, string currency, string units, string zone, int month, string code) =>
        TenantSettings.Create(legalName, colour, language, currency, units, zone, month).Error!.Code.Should().Be(code);

    [Theory]
    [InlineData("#767676", true)] // 4.54:1, the lightest grey that carries white text
    [InlineData("#777777", false)] // 4.48:1
    [InlineData("#000000", true)]
    [InlineData("#FFFFFF", false)]
    public void A_brand_colour_must_carry_white_text(string colour, bool accepted) =>
        TenantSettings.Create("Carrière", colour, "fr", "EUR", "metric", "UTC", 1).IsSuccess.Should().Be(accepted);

    [Fact]
    public void Changing_to_the_same_settings_raises_nothing()
    {
        var tenant = Tenant.Provision(TimeProvider.System, "carriere-a", "Carrière A").Value;
        tenant.ClearDomainEvents();

        tenant.ChangeSettings(tenant.Settings, "admin", DateTimeOffset.UnixEpoch).Should().BeFalse();
        tenant.DomainEvents.Should().BeEmpty();

        var next = tenant.Settings with { Currency = "TND" };
        tenant.ChangeSettings(next, "admin", DateTimeOffset.UnixEpoch).Should().BeTrue();
        tenant.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<TenantSettingsChanged>()
            .Which.Should().Match<TenantSettingsChanged>(e => e.Before.Currency == "EUR" && e.After.Currency == "TND" && e.Actor == "admin");
    }

    [Fact]
    public void A_new_company_starts_with_its_name_as_legal_name_and_the_defaults() =>
        Tenant.Provision(TimeProvider.System, "carriere-a", "Carrière A").Value.Settings
            .Should().Be(new TenantSettings("Carrière A", null, "fr", "EUR", UnitSystem.Metric, "UTC", 1));

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50, 0x56 }, "image/webp")]
    public void Logos_are_recognised_by_their_content(byte[] content, string type) =>
        TenantLogo.Create(Guid.NewGuid(), content, DateTimeOffset.UnixEpoch).Value.ContentType.Should().Be(type);

    [Fact]
    public void Svg_unknown_and_oversized_logos_are_refused()
    {
        TenantLogo.Create(Guid.NewGuid(), "<svg onload=\"alert(1)\"/>"u8, DateTimeOffset.UnixEpoch).Error!.Code.Should().Be("tenant.logo_type_unsupported");
        TenantLogo.Create(Guid.NewGuid(), [], DateTimeOffset.UnixEpoch).Error!.Code.Should().Be("tenant.logo_type_unsupported");

        var big = new byte[TenantLogo.MaxBytes + 1];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(big, 0);
        TenantLogo.Create(Guid.NewGuid(), big, DateTimeOffset.UnixEpoch).Error!.Code.Should().Be("tenant.logo_too_large");
        TenantLogo.Create(Guid.NewGuid(), big.AsSpan(0, TenantLogo.MaxBytes), DateTimeOffset.UnixEpoch).IsSuccess.Should().BeTrue("exactly the limit");
    }
}
