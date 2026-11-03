using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Trust.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Trust.Application;

public sealed record AnchoringRun(int Tenants, int Anchored, int Failed);

/// <summary>
/// Hourly anchoring (US-P1-AUD-02): for every tenant whose chain head moved since its last anchor, sign the head and
/// write it to WORM storage, then record it. Catch-up is inherent: after an outage the next run anchors the current
/// head, which covers every entry written meanwhile (TC-P1-AUD-004). One tenant's failure does not stop the others;
/// the run reports it (metric + alert KarryLedgerAnchoringStale). Runs never overlap, even in different processes
/// (<see cref="IAnchoringLock"/>): a second run waits, then finds every head already anchored.
/// </summary>
public sealed partial class AnchorLedgers(IServiceScopeFactory scopes, IAnchorSigner signer, IAnchorStore store, TimeProvider clock,
    AnchoringMetrics metrics, IAnchoringLock runs, ILogger<AnchorLedgers> logger)
{
    public async Task<AnchoringRun> RunAsync(CancellationToken cancellationToken)
    {
        // Held for the whole run: each tenant's "last anchored" is read after the previous run recorded its anchors.
        await using var exclusive = await runs.AcquireAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SharedKernel.Tenancy.TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var anchored = 0;
        var failed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                var books = scope.ServiceProvider.GetRequiredService<IAnchorBookkeeping>();
                if (await books.HeadAsync(cancellationToken).ConfigureAwait(false) is not { } head
                    || head.Sequence <= await books.LastAnchoredSequenceAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue; // nothing new since the last anchor
                }

                var anchor = new LedgerAnchor(tenant, head.Sequence, head.Hash, LedgerHash.ToStoragePrecision(clock.GetUtcNow()), signer.KeyId);
                await store.PutAsync(anchor, anchor.ToDocument(signer.Sign(anchor.SignedBytes())), cancellationToken).ConfigureAwait(false);
                await books.RecordAsync(anchor, cancellationToken).ConfigureAwait(false);
                anchored++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogTenantFailed(logger, ex, tenant.ToString());
            }
        }

        metrics.RunCompleted(failed == 0, clock.GetUtcNow());
        return new AnchoringRun(tenants.Count, anchored, failed);
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Error, Message = "Ledger anchoring failed for tenant {Tenant}; the next run retries")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, string tenant);
}
