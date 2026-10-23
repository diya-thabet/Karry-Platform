using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Tenancy.Domain;

/// <summary>
/// The tenant's address label: <c>acme</c> in <c>https://acme.karry.app</c> (US-P1-TEN-01 AC3). Lowercase letters,
/// digits and inner hyphens, 3–40 characters (a valid DNS label), not a reserved platform name. Input is trimmed and
/// lowercased; the slug never changes after provisioning (links, bookmarks and certificates depend on it).
/// </summary>
public sealed partial record TenantSlug
{
    public const int MinLength = 3;
    public const int MaxLength = 40;

    /// <summary>Platform and infrastructure names a customer can never take.</summary>
    public static readonly FrozenSet<string> Reserved = FrozenSet.Create(StringComparer.Ordinal,
    [
        "www", "api", "app", "admin", "auth", "login", "sso", "id", "identity", "keycloak", "portal", "field",
        "status", "docs", "help", "support", "mail", "smtp", "cdn", "static", "assets", "grafana", "metrics",
        "staging", "dev", "test", "demo", "platform", "operator", "karry", "billing", "security",
    ]);

    private TenantSlug(string value) => Value = value;

    public string Value { get; }

    public static Result<TenantSlug> Create(string? input)
    {
        var value = (input ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is < MinLength or > MaxLength || !Pattern().IsMatch(value))
        {
            return Error.Validation("tenant.slug_invalid",
                $"The address must be {MinLength}–{MaxLength} lowercase letters, digits or inner hyphens (e.g. carriere-sfax).");
        }

        if (Reserved.Contains(value))
        {
            return Error.Validation("tenant.slug_reserved", $"'{value}' is reserved by the platform.");
        }

        return new TenantSlug(value);
    }

    /// <summary>Rehydration from storage (already validated when it was created).</summary>
    public static TenantSlug FromStorage(string value) => new(value);

    public override string ToString() => Value;

    // Starts and ends with a letter or digit; hyphens only inside, never doubled (no "xn--" punycode look-alikes).
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?!-))*[a-z0-9]$")]
    private static partial Regex Pattern();
}
