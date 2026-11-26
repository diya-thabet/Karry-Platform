using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Notifications.Application;

/// <summary>
/// Notices are kept 90 days (ADR-0039 §1): they are not business facts (the events they come from are in the ledger), so
/// older notifications, e-mail deliveries and pushes are deleted, company by company, every night.
/// </summary>
public sealed partial class PurgeNotices(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PurgeNotices> logger)
{
    public static readonly TimeSpan Kept = TimeSpan.FromDays(90);

    public Task<int> RunAsync(CancellationToken cancellationToken) => RunAtAsync(clock.GetUtcNow(), cancellationToken);

    /// <summary>Deletes what was created before <paramref name="now"/> minus 90 days; returns how many rows went.</summary>
    public async Task<int> RunAtAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> companies;
        await using (var scope = scopes.CreateAsyncScope())
        {
            companies = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var deleted = 0;
        foreach (var company in companies)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(company);
                deleted += await scope.ServiceProvider.GetRequiredService<INotificationStore>().DeleteBeforeAsync(now - Kept, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex, company.Value);
            }
        }

        return deleted;
    }

    [LoggerMessage(EventId = 7606, Level = LogLevel.Error, Message = "Deleting the old notices of tenant {Tenant} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenant);
}
