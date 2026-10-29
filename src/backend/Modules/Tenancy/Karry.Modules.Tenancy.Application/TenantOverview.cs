using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Domain;
using Karry.SharedKernel.Entitlements;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Tenancy.Application;

/// <summary>
/// One tenant of the operator console: its counts by metric, what the modules tell about it, and whether every module
/// answered (<see cref="Complete"/> false: a module failed for this tenant and its columns are empty).
/// </summary>
public sealed record TenantOverviewRow(Tenant Tenant, IReadOnlyDictionary<string, long> Usage, TenantSummaryPart Summary, bool Complete);

/// <summary>
/// The operator console's tenant list (US-P1-OPS-03 AC1, ADR-0029 §3). Tenants come from Tenancy's own directory; each
/// is then summarized in its own scope with <see cref="ITenantContext"/> set to it, as the anchoring job does, so
/// row-level security is never bypassed and each module answers only from its own tables: counts through
/// <see cref="IUsageSource"/>, other columns through <see cref="ITenantSummarySource"/>. One tenant at a time: fine up
/// to about a thousand tenants (beyond, a nightly read model; ADR-0029 Consequences).
/// </summary>
public sealed partial class TenantOverview(ITenantRepository tenants, IServiceScopeFactory scopes, ILogger<TenantOverview> logger)
{
    /// <summary>The counts the list shows.</summary>
    public static readonly IReadOnlyList<string> Counted = [Metrics.Sites, Metrics.Assets, Metrics.OfficeUsers];

    public async Task<IReadOnlyList<TenantOverviewRow>> ListAsync(CancellationToken cancellationToken)
    {
        var all = await tenants.ListAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<TenantOverviewRow>(all.Count);
        foreach (var tenant in all)
        {
            rows.Add(await SummarizeAsync(tenant, cancellationToken).ConfigureAwait(false));
        }

        return rows;
    }

    private async Task<TenantOverviewRow> SummarizeAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant.Id);
            var usage = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var source in scope.ServiceProvider.GetServices<IUsageSource>().Where(s => Counted.Contains(s.Metric, StringComparer.Ordinal)))
            {
                usage[source.Metric] = await source.CountAsync(cancellationToken).ConfigureAwait(false);
            }

            var summary = new TenantSummaryPart();
            var request = new TenantSummaryRequest(tenant.Id.Value, tenant.Currency);
            foreach (var source in scope.ServiceProvider.GetServices<ITenantSummarySource>())
            {
                summary = summary.With(await source.SummarizeAsync(request, cancellationToken).ConfigureAwait(false));
            }

            return new TenantOverviewRow(tenant, usage, summary, Complete: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One broken tenant must not hide the others: its row says it is incomplete.
            LogTenantFailed(logger, ex, tenant.Id.ToString());
            return new TenantOverviewRow(tenant, new Dictionary<string, long>(StringComparer.Ordinal), new TenantSummaryPart(), Complete: false);
        }
    }

    [LoggerMessage(EventId = 6001, Level = LogLevel.Error, Message = "The operator console could not summarize tenant {Tenant}")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, string tenant);
}
