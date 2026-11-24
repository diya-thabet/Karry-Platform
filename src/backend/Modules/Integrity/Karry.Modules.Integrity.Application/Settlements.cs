using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Integrity.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Karry.Modules.Integrity.Application;

/// <summary>The current company's flags as settlements change them (RLS).</summary>
public interface IFlagStore
{
    Task<IntegrityFlag?> FindAsync(IntegrityFlagId id, CancellationToken cancellationToken);

    /// <summary>Open continuity gaps opened at or before <paramref name="openedBy"/> with no RULE-FO-11 raised yet, oldest first.</summary>
    Task<IReadOnlyList<IntegrityFlag>> UnsettledGapsAsync(DateTimeOffset openedBy, int take, CancellationToken cancellationToken);

    void Add(IntegrityFlag flag);

    /// <summary>False when someone else changed one of the flags first (<c>xmin</c>): nothing is saved.</summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);
}

/// <summary>What a person says when settling a continuity gap: the reason code and what happened.</summary>
public sealed record SettlementInput(string Reason, string? Comment);

/// <summary>What a person may say when declining a settlement.</summary>
public sealed record SettlementDeclineInput(string? Comment);

/// <summary>
/// Continuity gaps settled with a reason (US-P2-FO-08, ADR-0037 §7): one person proposes, another approves, each with
/// <see cref="IntegrityPermissions.FlagsSettle"/> on the flag's site and a recent sign-in. "Unexplained" raises
/// RULE-FO-11 at once: suspicion does not wait for the second person. Every step is ledgered under the person.
/// </summary>
public sealed class FlagSettlements(IFlagStore store, ICallerAccess caller, TimeProvider clock)
{
    public static readonly Error NotFound = Error.NotFound("integrity.flag_not_found", "No such flag.");

    public Task<Result<IntegrityFlag>> ProposeAsync(Guid flagId, SettlementInput input, uint? expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ActAsync(flagId, expectedVersion, stepUp: true, flag =>
        {
            if (flag.ProposeSettlement(clock, input.Reason, input.Comment, caller.Subject) is { } refused)
            {
                return refused;
            }

            if (input.Reason == SettlementReasons.Unexplained && flag.SuspectFraud(clock, IntegrityFlag.FraudUnexplained) is { } fraud)
            {
                store.Add(fraud);
            }

            return null;
        }, cancellationToken);
    }

    public Task<Result<IntegrityFlag>> ConfirmAsync(Guid flagId, uint? expectedVersion, CancellationToken cancellationToken) =>
        ActAsync(flagId, expectedVersion, stepUp: true, flag => flag.ConfirmSettlement(clock, caller.Subject), cancellationToken);

    /// <summary>Declining keeps the gap open: no recent sign-in needed, nothing is hidden by it.</summary>
    public Task<Result<IntegrityFlag>> DeclineAsync(Guid flagId, SettlementDeclineInput input, uint? expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ActAsync(flagId, expectedVersion, stepUp: false, flag => flag.DeclineSettlement(clock, input.Comment, caller.Subject), cancellationToken);
    }

    private async Task<Result<IntegrityFlag>> ActAsync(
        Guid flagId, uint? expectedVersion, bool stepUp, Func<IntegrityFlag, Error?> act, CancellationToken cancellationToken)
    {
        // The flag first: outside the caller's sites (or company) it does not exist, whatever the request says. A flag
        // without a site belongs to the company: only a company-wide scope settles it.
        var flag = await store.FindAsync(new IntegrityFlagId(flagId), cancellationToken).ConfigureAwait(false);
        var scope = caller.Current.ScopeOf(IntegrityPermissions.FlagsSettle);
        if (flag is null || !(flag.SiteId is { } site ? scope.CoversSite(site) : scope.TenantWide))
        {
            return NotFound;
        }

        if (!RecordVersions.Matches(flag.Version, expectedVersion))
        {
            return RecordVersions.Conflict;
        }

        if (stepUp && StepUp.Check(caller, clock) is { } required)
        {
            return required;
        }

        if (act(flag) is { } refused)
        {
            return refused;
        }

        return await store.TrySaveAsync(cancellationToken).ConfigureAwait(false) ? flag : RecordVersions.Conflict;
    }
}

/// <summary>What a run did: RULE-FO-11 flags raised, companies that failed.</summary>
public sealed record FraudSuspicionRun(int Raised, int FailedTenants);

/// <summary>
/// Continuity gaps nobody settled within 48 h raise RULE-FO-11 <c>FRAUD_SUSPECTED</c> (US-P2-FO-08 AC2, ADR-0037 §7),
/// once per gap, ledgered. Run by the Workers every 15 minutes; a failing company is logged and the others still run.
/// </summary>
public sealed partial class RaiseUnsettledGaps(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RaiseUnsettledGaps> logger)
{
    /// <summary>The most gaps one run takes per company; the next run takes the rest (a gap is taken once).</summary>
    public const int Batch = 500;

    public Task<FraudSuspicionRun> RunAsync(CancellationToken cancellationToken) => RunAtAsync(clock.GetUtcNow(), cancellationToken);

    /// <summary>The run as of <paramref name="now"/> (tests move time forward without a fake clock).</summary>
    public async Task<FraudSuspicionRun> RunAtAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantId> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken).ConfigureAwait(false);
        }

        var raised = 0;
        var failed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
                var store = scope.ServiceProvider.GetRequiredService<IFlagStore>();
                var count = 0;
                foreach (var gap in await store.UnsettledGapsAsync(now - IntegrityFlag.SettleWithin, Batch, cancellationToken).ConfigureAwait(false))
                {
                    if (gap.SuspectFraud(clock, IntegrityFlag.FraudUnsettled) is { } fraud)
                    {
                        store.Add(fraud);
                        count++;
                    }
                }

                if (count > 0 && await store.TrySaveAsync(cancellationToken).ConfigureAwait(false))
                {
                    raised += count;
                    LogRaised(logger, tenant.Value, count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogFailed(logger, ex, tenant.Value);
            }
        }

        return new FraudSuspicionRun(raised, failed);
    }

    [LoggerMessage(EventId = 7401, Level = LogLevel.Warning, Message = "Raised FRAUD_SUSPECTED on {Count} continuity gaps of tenant {Tenant} left unsettled beyond 48 h")]
    private static partial void LogRaised(ILogger logger, Guid tenant, int count);

    [LoggerMessage(EventId = 7402, Level = LogLevel.Error, Message = "Raising FRAUD_SUSPECTED on the unsettled continuity gaps of tenant {Tenant} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenant);
}
