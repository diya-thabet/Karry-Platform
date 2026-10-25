using System.Globalization;
using System.Text.RegularExpressions;
using Karry.SharedKernel.Domain;
using Karry.SharedKernel.Identity;
using Karry.SharedKernel.Monetary;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Billing.Domain;

public readonly record struct PlanId(Guid Value) : IEntityId<PlanId>
{
    public static PlanId From(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}

public enum BillingPeriod
{
    Monthly = 1,
    Annual = 2,
}

/// <summary>
/// One line of a plan's price book (US-P1-BIL-01 AC1): the price per site and per extra asset in one currency for one
/// period. A price book, not an FX conversion: each currency is priced on purpose. Amounts are decimals in the
/// currency's minor units.
/// </summary>
public sealed record PlanPrice(string Currency, BillingPeriod Period, decimal PerSite, decimal? ExtraAsset);

/// <summary>A plan version was published (platform data: no tenant ledger; the outbox tells other instances).</summary>
[EventType("billing.plan_published.v1")]
public sealed record PlanPublished(Guid PlanId, string Code, int Version, string? Actor) : DomainEvent;

/// <summary>
/// One <b>version</b> of a plan (ADR-0023, US-P1-BIL-01). A published version never changes: new packaging or prices
/// are version n + 1, and subscriptions stay on the version they signed until an operator moves them (AC2). Features
/// and limits name codes declared in <see cref="SharedKernel.Entitlements.Features"/> and <see cref="SharedKernel.Entitlements.Limits"/>; a limit left out is unlimited.
/// Enterprise-style plans have no prices (quoted by sales).
/// </summary>
public sealed partial class Plan : AggregateRoot<PlanId>
{
    public const int NameMaxLength = 60;

    private Plan(PlanId id, string code, int version, string name, IReadOnlyList<string> features, IReadOnlyDictionary<string, int> limits,
        IReadOnlyList<PlanPrice> prices, DateTimeOffset publishedAt)
        : base(id)
    {
        Code = code;
        Version = version;
        Name = name;
        Features = features;
        Limits = limits;
        Prices = prices;
        PublishedAt = publishedAt;
    }

    /// <summary>Stable code across versions, e.g. <c>professional</c>.</summary>
    public string Code { get; private set; }

    /// <summary>1, 2, … per code.</summary>
    public int Version { get; private set; }

    public string Name { get; private set; }

    /// <summary>Feature codes, sorted.</summary>
    public IReadOnlyList<string> Features { get; private set; }

    /// <summary>Limit code → value; a code not present is unlimited.</summary>
    public IReadOnlyDictionary<string, int> Limits { get; private set; }

    public IReadOnlyList<PlanPrice> Prices { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }

    public bool Includes(string feature) => Features.Contains(feature, StringComparer.Ordinal);

    /// <summary>The next version of <paramref name="code"/> (<paramref name="latest"/> is the current one, null for a new plan).</summary>
    public static Result<Plan> Publish(TimeProvider clock, string? code, Plan? latest, string? name, IEnumerable<string>? features,
        IReadOnlyDictionary<string, int>? limits, IEnumerable<PlanPrice>? prices, string? actor)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (code is null || !CodePattern().IsMatch(code))
        {
            return Error.Validation("billing.plan_code_invalid", "A plan code is 2–40 lowercase letters, digits or hyphens, starting with a letter.");
        }

        if (latest is not null && !string.Equals(latest.Code, code, StringComparison.Ordinal))
        {
            throw new ArgumentException("The latest version belongs to another plan.", nameof(latest));
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > NameMaxLength || trimmed.Any(char.IsControl))
        {
            return Error.Validation("billing.plan_name_invalid", $"A plan name is 2–{NameMaxLength} characters.");
        }

        var featureList = (features ?? []).ToList();
        if (featureList.FirstOrDefault(f => !SharedKernel.Entitlements.Features.IsKnown(f)) is { } unknownFeature)
        {
            return Error.Validation("billing.feature_unknown", $"Unknown feature '{unknownFeature}'.");
        }

        var limitMap = limits ?? new Dictionary<string, int>();
        if (limitMap.Keys.FirstOrDefault(l => !SharedKernel.Entitlements.Limits.IsKnown(l)) is { } unknownLimit)
        {
            return Error.Validation("billing.limit_unknown", $"Unknown limit '{unknownLimit}'.");
        }

        if (limitMap.Values.Any(v => v < 0))
        {
            return Error.Validation("billing.limit_invalid", "A limit is zero or more (leave it out for unlimited).");
        }

        var priceList = (prices ?? []).ToList();
        if (ValidatePrices(priceList) is { } priceError)
        {
            return priceError;
        }

        var now = clock.GetUtcNow();
        var plan = new Plan(EntityId.New<PlanId>(clock), code, (latest?.Version ?? 0) + 1, trimmed,
            [.. featureList.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            limitMap.OrderBy(l => l.Key, StringComparer.Ordinal).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal),
            [.. priceList.OrderBy(p => p.Currency, StringComparer.Ordinal).ThenBy(p => p.Period)], now);
        plan.Raise(new PlanPublished(plan.Id.Value, plan.Code, plan.Version, actor) { OccurredAt = now });
        return plan;
    }

    private static Error? ValidatePrices(List<PlanPrice> prices)
    {
        foreach (var price in prices)
        {
            if (!Currency.SupportedCodes.Contains(price.Currency, StringComparer.Ordinal))
            {
                return Error.Validation("billing.currency_unsupported", $"Unsupported currency '{price.Currency}'.");
            }

            if (!Enum.IsDefined(price.Period))
            {
                return Error.Validation("billing.price_invalid", "A price is monthly or annual.");
            }

            var minor = Currency.Of(price.Currency).MinorUnits;
            if (!IsAmount(price.PerSite, minor) || (price.ExtraAsset is { } extra && !IsAmount(extra, minor)))
            {
                return Error.Validation("billing.price_invalid",
                    string.Create(CultureInfo.InvariantCulture, $"Prices in {price.Currency} are zero or more with at most {minor} decimals."));
            }
        }

        return prices.GroupBy(p => (p.Currency, p.Period)).Any(g => g.Count() > 1)
            ? Error.Validation("billing.price_duplicate", "One price per currency and period.")
            : null;
    }

    private static bool IsAmount(decimal amount, int minorUnits) => amount >= 0 && decimal.Round(amount, minorUnits) == amount;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
