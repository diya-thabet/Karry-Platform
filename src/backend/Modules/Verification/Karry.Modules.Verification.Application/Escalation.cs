using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Verification.Application;

/// <summary>What an escalation run did: items escalated, companies that failed.</summary>
public sealed record EscalationRun(int Escalated, int FailedTenants);

/// <summary>
/// The SLA of reviews (US-P2-VER-06 AC1, ADR-0037 §6): every company's items still waiting after their due time are
/// escalated once to the role of the policy they started under, ledgered. Run by the Workers every 5 minutes; a
/// failing company is logged and the others still run.
/// </summary>
public sealed partial class EscalateOverdueReviews(IServiceScopeFactory scopes, TimeProvider clock, ILogger<EscalateOverdueReviews> logger)
{
    /// <summary>The most items one run escalates per company; the next run takes the rest.</summary>
    public const int Batch = 500;

    public Task<EscalationRun> RunAsync(CancellationToken cancellationToken) => RunAtAsync(clock.GetUtcNow(), cancellationToken);

    /// <summary>The run as of <paramref name="now"/> (tests move time forward without a fake clock).</summary>
    public async Task<EscalationRun> RunAtAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var escalated = 0;
        var failed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                var store = scope.ServiceProvider.GetRequiredService<IReviewItemStore>();
                var policies = scope.ServiceProvider.GetRequiredService<IReviewPolicyStore>();
                var count = 0;
                foreach (var item in await store.OverdueAsync(now, Batch, cancellationToken).ConfigureAwait(false))
                {
                    var policy = await policies.VersionAsync(item.RecordType, item.PolicyVersion, cancellationToken).ConfigureAwait(false);
                    if (item.Escalate(clock, now, policy.Terms.EscalateTo) is { } action)
                    {
                        store.Add(action);
                        count++;
                    }
                }

                if (count > 0 && await store.TrySaveAsync(cancellationToken).ConfigureAwait(false))
                {
                    escalated += count;
                    LogEscalated(logger, tenant.Value, count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogFailed(logger, ex, tenant.Value);
            }
        }

        return new EscalationRun(escalated, failed);
    }

    [LoggerMessage(EventId = 7301, Level = LogLevel.Information, Message = "Escalated {Count} overdue review items of tenant {Tenant}")]
    private static partial void LogEscalated(ILogger logger, Guid tenant, int count);

    [LoggerMessage(EventId = 7302, Level = LogLevel.Error, Message = "Escalating the overdue review items of tenant {Tenant} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenant);
}
