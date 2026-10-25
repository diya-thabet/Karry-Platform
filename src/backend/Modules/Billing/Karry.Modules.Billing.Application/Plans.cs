using Karry.Modules.Billing.Domain;
using Karry.SharedKernel.Results;

namespace Karry.Modules.Billing.Application;

/// <summary>The plan catalogue (platform data).</summary>
public interface IPlanStore
{
    /// <summary>The newest version of each plan, by code.</summary>
    Task<IReadOnlyList<Plan>> LatestAsync(CancellationToken cancellationToken);

    /// <summary>Every version of one plan, newest first (empty for an unknown code).</summary>
    Task<IReadOnlyList<Plan>> VersionsAsync(string code, CancellationToken cancellationToken);

    /// <summary>Saves a new version; false when another operator published the same version first.</summary>
    Task<bool> TryAddAsync(Plan plan, CancellationToken cancellationToken);
}

/// <summary>A new version of a plan (or its first): the full content, nothing carried over implicitly.</summary>
public sealed record PublishPlan(string Code, string Name, IReadOnlyList<string>? Features, IReadOnlyDictionary<string, int>? Limits,
    IReadOnlyList<PlanPriceDto>? Prices);

/// <summary>A price book line as the API writes it: <c>period</c> is <c>monthly</c> or <c>annual</c>.</summary>
public sealed record PlanPriceDto(string Currency, string Period, decimal PerSite, decimal? ExtraAsset)
{
    public static PlanPriceDto Of(PlanPrice price)
    {
        ArgumentNullException.ThrowIfNull(price);
        return new(price.Currency, PeriodName(price.Period), price.PerSite, price.ExtraAsset);
    }

    public static string PeriodName(BillingPeriod period) => period == BillingPeriod.Annual ? "annual" : "monthly";

    /// <summary>The period of <paramref name="name"/>; an unknown name gives an undefined value, which the domain refuses.</summary>
    public static BillingPeriod ParsePeriod(string? name) => name switch
    {
        "monthly" => BillingPeriod.Monthly,
        "annual" => BillingPeriod.Annual,
        _ => 0,
    };
}

/// <summary>
/// Publishes the next version of a plan (US-P1-BIL-01 AC1–AC2). Existing subscriptions keep their version. Two
/// operators publishing at once: one wins, the other gets 409 and publishes again on top of it.
/// </summary>
public sealed class PublishPlanHandler(IPlanStore plans, TimeProvider clock)
{
    public static readonly Error Concurrent = Error.Conflict("billing.plan_version_conflict",
        "Another version of this plan was published at the same time; reload and publish again.");

    public async Task<Result<Plan>> HandleAsync(PublishPlan command, string? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var versions = await plans.VersionsAsync(command.Code, cancellationToken).ConfigureAwait(false);
        var plan = Plan.Publish(clock, command.Code, versions.Count > 0 ? versions[0] : null, command.Name, command.Features, command.Limits,
            command.Prices?.Select(p => new PlanPrice(p.Currency, PlanPriceDto.ParsePeriod(p.Period), p.PerSite, p.ExtraAsset)), actor);
        if (!plan.IsSuccess)
        {
            return plan.Error;
        }

        return await plans.TryAddAsync(plan.Value, cancellationToken).ConfigureAwait(false) ? plan.Value : Concurrent;
    }
}
