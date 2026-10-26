using System.Globalization;
using Karry.SharedKernel.Results;
using NodaTime;
using SupportedCurrency = Karry.SharedKernel.Monetary.Currency;

namespace Karry.Modules.Tenancy.Domain;

/// <summary>Units the company works in by default (US-P1-TEN-04); every stored quantity still carries its own unit.</summary>
public enum UnitSystem
{
    Metric = 1,
    Imperial = 2,
}

/// <summary>
/// A company's settings (US-P1-TEN-04 AC1): legal name, brand colour, default language, functional currency, unit system,
/// default time zone and fiscal year start. Values are validated here; the logo is a <see cref="TenantLogo"/>.
/// </summary>
public sealed record TenantSettings(
    string LegalName, string? PrimaryColour, string Language, string Currency, UnitSystem UnitSystem, string TimeZone, int FiscalYearStartMonth)
{
    public const int LegalNameMaxLength = 200;

    /// <summary>Languages the apps are translated into (ADR-0020 catalogs).</summary>
    public static IReadOnlyList<string> Languages { get; } = ["fr", "en"];

    /// <summary>
    /// Brand colours must carry white text (the apps' header and the sign-in button): contrast at least 4.5:1 against
    /// white (WCAG 2.2 AA, 1.4.3).
    /// </summary>
    public const double MinimumContrastWithWhite = 4.5;

    /// <summary>Settings of a new company until its administrator changes them.</summary>
    public static TenantSettings DefaultsFor(string name) => new(name, null, "fr", "EUR", UnitSystem.Metric, "UTC", 1);

    public static Result<TenantSettings> Create(
        string? legalName, string? primaryColour, string? language, string? currency, string? unitSystem, string? timeZone, int fiscalYearStartMonth)
    {
        var name = string.Join(' ', (legalName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is < 2 or > LegalNameMaxLength || name.Any(char.IsControl))
        {
            return Error.Validation("tenant.legal_name_invalid", $"The legal name must be 2–{LegalNameMaxLength} characters.");
        }

        var colour = string.IsNullOrWhiteSpace(primaryColour) ? null : primaryColour.Trim().ToUpperInvariant();
        if (colour is not null)
        {
            if (!TryParseColour(colour, out var red, out var green, out var blue))
            {
                return Error.Validation("tenant.colour_invalid", "The colour must be written #RRGGBB.");
            }

            if (ContrastWithWhite(red, green, blue) < MinimumContrastWithWhite)
            {
                return Error.Validation("tenant.colour_contrast", "This colour is too light for white text; choose a darker shade (contrast at least 4.5:1).")
                    .With("minimumContrast", MinimumContrastWithWhite);
            }
        }

        if (language is null || !Languages.Contains(language, StringComparer.Ordinal))
        {
            return Error.Validation("tenant.language_unsupported", $"The language must be one of: {string.Join(", ", Languages)}.");
        }

        if (currency is null || !SupportedCurrency.SupportedCodes.Contains(currency))
        {
            return Error.Validation("tenant.currency_unsupported", $"The currency must be one of: {string.Join(", ", SupportedCurrency.SupportedCodes.Order(StringComparer.Ordinal))}.");
        }

        if (!Enum.TryParse<UnitSystem>(unitSystem, ignoreCase: true, out var units) || !Enum.IsDefined(units) || int.TryParse(unitSystem, out _))
        {
            return Error.Validation("tenant.unit_system_invalid", "The unit system must be metric or imperial.");
        }

        if (timeZone is null || DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) is null)
        {
            return Error.Validation("tenant.time_zone_unknown", "The time zone must be an IANA name such as Africa/Tunis.");
        }

        if (fiscalYearStartMonth is < 1 or > 12)
        {
            return Error.Validation("tenant.fiscal_month_invalid", "The fiscal year starts in a month from 1 to 12.");
        }

        return new TenantSettings(name, colour, language, currency, units, timeZone, fiscalYearStartMonth);
    }

    /// <summary>WCAG 2.2 contrast ratio of white against the colour.</summary>
    public static double ContrastWithWhite(int red, int green, int blue) => 1.05 / (RelativeLuminance(red, green, blue) + 0.05);

    private static double RelativeLuminance(int red, int green, int blue) =>
        (0.2126 * Linear(red)) + (0.7152 * Linear(green)) + (0.0722 * Linear(blue));

    private static double Linear(int channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static bool TryParseColour(string colour, out int red, out int green, out int blue)
    {
        red = green = blue = 0;
        return colour.Length == 7 && colour[0] == '#'
            && int.TryParse(colour.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out red)
            && int.TryParse(colour.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out green)
            && int.TryParse(colour.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out blue);
    }
}
