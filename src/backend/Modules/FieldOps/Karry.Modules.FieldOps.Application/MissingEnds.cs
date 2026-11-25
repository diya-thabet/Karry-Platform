using System.Globalization;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.FieldOps.Domain;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.FieldOps.Application;

/// <summary>Segments of the current company that started and never ended, and a transaction to flag them in (RLS).</summary>
public interface IOpenSegments
{
    /// <summary>
    /// The current versions still open whose shift ended between <paramref name="since"/> and <paramref name="by"/>,
    /// the most recent shift first.
    /// </summary>
    Task<IReadOnlyList<ShiftEntryVersion>> UnendedAsync(DateTimeOffset since, DateTimeOffset by, int take, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="work"/> in one transaction of the FieldOps database, committed when it returns.</summary>
    Task InTransactionAsync(Func<ISyncTransaction, Task> work, CancellationToken cancellationToken);
}

/// <summary>
/// RULE-FO-08 "missing end" (US-P2-VER-08 AC2, ADR-0038 §5) for the current company: a segment still open the
/// rule's <c>graceMinutes</c> after its shift ended raises a flag on the entry (key <c>missing_end</c>); the end, when it
/// arrives, closes it (the end handler syncs the rule with no finding). Shifts that ended more than 14 days ago are not
/// looked at again: their flag was raised then and stays open until the end arrives.
/// </summary>
public sealed class MissingEndCheck(IOpenSegments segments, IIntegrityFlags integrity)
{
    public const string Key = "missing_end";

    /// <summary>The most entries one run flags per company, newest shifts first; the next run takes the rest.</summary>
    public const int Batch = 500;

    public static readonly TimeSpan LookBack = TimeSpan.FromDays(14);

    /// <summary>Raises the flag on every segment overdue as of <paramref name="now"/>; returns how many were looked at.</summary>
    public async Task<int> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = await integrity.SettingsAsync(IntegrityRules.MissingEnd, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return 0;
        }

        var grace = TimeSpan.FromMinutes((double)settings.Thresholds["graceMinutes"]);
        var overdue = await segments.UnendedAsync(now - grace - LookBack, now - grace, Batch, cancellationToken).ConfigureAwait(false);
        if (overdue.Count == 0)
        {
            return 0;
        }

        await segments.InTransactionAsync(async transaction =>
        {
            foreach (var segment in overdue)
            {
                await integrity.SyncAsync(
                    transaction.Connection, transaction.Transaction, new FlagSubject(IntegrityFindings.ShiftEntry, segment.RootId), IntegrityRules.MissingEnd, segment.SiteId,
                    [Finding(segment, grace)], cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        return overdue.Count;
    }

    /// <summary>What the flag says: when the shift ended and the grace the company gives.</summary>
    public static Finding Finding(ShiftEntryVersion segment, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new Finding(Key, [new FlagSubject("operator", segment.OperatorId)], [], new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["shiftEndsAt"] = segment.ShiftEndsAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
            ["graceMinutes"] = ((int)grace.TotalMinutes).ToString(CultureInfo.InvariantCulture),
        });
    }
}

/// <summary>What a run did: entries looked at, companies that failed.</summary>
public sealed record MissingEndRun(int Checked, int FailedTenants);

/// <summary>Runs <see cref="MissingEndCheck"/> for every company (Workers, every 5 minutes); a failing company is logged and the others still run.</summary>
public sealed partial class RaiseMissingEnds(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RaiseMissingEnds> logger)
{
    public Task<MissingEndRun> RunAsync(CancellationToken cancellationToken) => RunAtAsync(clock.GetUtcNow(), cancellationToken);

    public async Task<MissingEndRun> RunAtAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var looked = 0;
        var failed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                looked += await scope.ServiceProvider.GetRequiredService<MissingEndCheck>().RunAsync(now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogFailed(logger, ex, tenant.Value);
            }
        }

        return new MissingEndRun(looked, failed);
    }

    [LoggerMessage(EventId = 7501, Level = LogLevel.Error, Message = "Raising RULE-FO-08 on the unended segments of tenant {Tenant} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenant);
}
