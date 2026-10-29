namespace Karry.BuildingBlocks.Tenancy;

/// <summary>The tenant a summary is asked for, and its currency (the one its MRR is shown in when its plan prices it).</summary>
public sealed record TenantSummaryRequest(Guid TenantId, string Currency);

/// <summary>
/// What one module tells the platform operator about the current tenant (ADR-0029 §3); a module leaves null what it
/// does not own. <see cref="Mrr"/>: monthly recurring revenue in <see cref="MrrCurrency"/> (zero when nothing is billed),
/// null when it cannot be priced.
/// </summary>
public sealed record TenantSummaryPart(
    string? PlanCode = null,
    int? PlanVersion = null,
    string? SubscriptionStatus = null,
    DateTimeOffset? TrialEndsAt = null,
    decimal? Mrr = null,
    string? MrrCurrency = null,
    DateTimeOffset? LastActivity = null)
{
    /// <summary>Both parts together: a value one part gives fills a gap of the other (each column has one owner).</summary>
    public TenantSummaryPart With(TenantSummaryPart other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(PlanCode ?? other.PlanCode, PlanVersion ?? other.PlanVersion, SubscriptionStatus ?? other.SubscriptionStatus,
            TrialEndsAt ?? other.TrialEndsAt, Mrr ?? other.Mrr, MrrCurrency ?? other.MrrCurrency, LastActivity ?? other.LastActivity);
    }
}

/// <summary>
/// A module's columns of the operator console's tenant list (ADR-0029 §3), like <c>IUsageSource</c> for counts: the
/// Tenancy module asks every source once per tenant, in a scope where <see cref="ITenantContext"/> is that tenant, so
/// row-level security applies and no module reads another's tables.
/// </summary>
public interface ITenantSummarySource
{
    Task<TenantSummaryPart> SummarizeAsync(TenantSummaryRequest request, CancellationToken cancellationToken);
}
