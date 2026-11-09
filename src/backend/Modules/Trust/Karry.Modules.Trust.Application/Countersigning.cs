using System.Diagnostics.Metrics;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Trust.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Trust.Application;

/// <summary>An envelope stored without its countersignature yet.</summary>
public sealed record PendingEnvelope(Guid EnvelopeId, string EnvelopeHash, DateTimeOffset ReceivedAt);

/// <summary>The current tenant's envelopes and their countersignatures (row-level security scopes it).</summary>
public interface ICountersignBook
{
    /// <summary>The oldest envelopes still pending, oldest first.</summary>
    Task<IReadOnlyList<PendingEnvelope>> PendingAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Stores the countersignature, once; false when another run already stored one for this envelope.</summary>
    Task<bool> RecordAsync(PendingEnvelope envelope, ServerSignature signature, DateTimeOffset signedAt, CancellationToken cancellationToken);
}

/// <summary>The server's public keys by <c>kid</c>, published so any verifier can check a countersignature (append-only).</summary>
public interface IServerKeyBook
{
    Task<IReadOnlySet<string>> KeyIdsAsync(CancellationToken cancellationToken);

    /// <summary>Adds the keys not yet published; never changes one.</summary>
    Task AddAsync(IReadOnlyList<ServerKey> keys, CancellationToken cancellationToken);
}

public sealed record CountersignRun(int Tenants, int Countersigned, TimeSpan? OldestPending, bool SignerDown, int Failed);

/// <summary>
/// Countersigns stored envelopes (ADR-0033 §4, US-P2-SIG-03): for each tenant, the pending envelopes oldest first, in
/// batches of <see cref="BatchSize"/>, signed with the server key over <see cref="Countersignatures.Digest"/>. A key
/// version not yet published is published first, so every countersignature can be verified from the database alone.
/// When the signing service does not answer, the run stops, envelopes stay pending, and the next run continues: the
/// field never waits for it (TC-P2-SIG-006). The age of the oldest pending envelope feeds the lag alert.
/// </summary>
public sealed partial class CountersignEnvelopes(IServiceScopeFactory scopes, IServerSigner signer, CountersignMetrics metrics, TimeProvider clock, ILogger<CountersignEnvelopes> logger)
{
    public const int BatchSize = 100;

    /// <summary>Batches per tenant and run, so one busy tenant does not hold the others back.</summary>
    public const int MaxBatchesPerTenant = 10;

    public async Task<CountersignRun> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SharedKernel.Tenancy.TenantId> tenants;
        HashSet<string> published;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
            published = [.. await scope.ServiceProvider.GetRequiredService<IServerKeyBook>().KeyIdsAsync(cancellationToken).ConfigureAwait(false)];
        }

        var countersigned = 0;
        var failed = 0;
        var down = !signer.Available;
        DateTimeOffset? oldest = null;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                var book = scope.ServiceProvider.GetRequiredService<ICountersignBook>();
                for (var batch = 0; !down && batch < MaxBatchesPerTenant; batch++)
                {
                    var pending = await book.PendingAsync(BatchSize, cancellationToken).ConfigureAwait(false);
                    foreach (var envelope in pending)
                    {
                        var signature = await signer.SignAsync(Countersignatures.Digest(envelope.EnvelopeHash, envelope.ReceivedAt), cancellationToken).ConfigureAwait(false);
                        if (!published.Contains(signature.KeyId))
                        {
                            await scope.ServiceProvider.GetRequiredService<IServerKeyBook>()
                                .AddAsync(await signer.KeysAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                            published.Add(signature.KeyId);
                        }

                        if (await book.RecordAsync(envelope, signature, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                        {
                            countersigned++;
                            metrics.Countersigned();
                        }
                    }

                    if (pending.Count < BatchSize)
                    {
                        break;
                    }
                }

                // What is left pending for this tenant (the lag), measured after the run's work.
                Track(await book.PendingAsync(1, cancellationToken).ConfigureAwait(false));
            }
            catch (ServerSignerUnavailableException ex)
            {
                // The service is down: stop signing for this run, but keep measuring the other tenants' lag.
                down = true;
                metrics.Failed();
                LogSignerDown(logger, ex);
                failed++;
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                Track(await scope.ServiceProvider.GetRequiredService<ICountersignBook>().PendingAsync(1, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                metrics.Failed();
                LogTenantFailed(logger, ex, tenant.ToString());
            }
        }

        var lag = oldest is { } o ? clock.GetUtcNow() - o : (TimeSpan?)null;
        metrics.Measured(lag ?? TimeSpan.Zero);
        return new CountersignRun(tenants.Count, countersigned, lag, down, failed);

        void Track(IReadOnlyList<PendingEnvelope> stillPending)
        {
            if (stillPending is [var first] && (oldest is null || first.ReceivedAt < oldest))
            {
                oldest = first.ReceivedAt;
            }
        }
    }

    [LoggerMessage(EventId = 4012, Level = LogLevel.Error, Message = "The signing service did not answer; envelopes stay pending until it does")]
    private static partial void LogSignerDown(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4013, Level = LogLevel.Error, Message = "Envelopes of tenant {Tenant} could not be countersigned")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, string tenant);
}

/// <summary>
/// Countersigning telemetry: <c>karry.trust.countersigned</c>, <c>karry.trust.countersign_failures</c> (alert
/// <c>KarryCountersignFailing</c>) and <c>karry.trust.countersign_lag</c>, the age of the oldest envelope still pending
/// (alert <c>KarryCountersignLagHigh</c> above 15 minutes; target 5). Nothing is reported before the first run.
/// </summary>
public sealed class CountersignMetrics
{
    public const string MeterName = "Karry.Trust";

    private readonly Counter<long> countersigned;
    private readonly Counter<long> failures;
    private double lagSeconds = -1;

    public CountersignMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        countersigned = meter.CreateCounter<long>("karry.trust.countersigned", "{envelope}", "Envelopes countersigned by the server key.");
        failures = meter.CreateCounter<long>("karry.trust.countersign_failures", "{failure}", "Countersign runs stopped by the signing service or a tenant's error.");
        meter.CreateObservableGauge("karry.trust.countersign_lag", () =>
            Volatile.Read(ref lagSeconds) is var lag and >= 0 ? [new Measurement<double>(lag)] : Array.Empty<Measurement<double>>(),
            "s", "Age of the oldest envelope not yet countersigned (0 when none).");
    }

    public void Countersigned() => countersigned.Add(1);

    public void Failed() => failures.Add(1);

    public void Measured(TimeSpan lag) => Volatile.Write(ref lagSeconds, Math.Max(0, lag.TotalSeconds));
}
