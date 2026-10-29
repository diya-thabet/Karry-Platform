using Karry.BuildingBlocks.Authorization;

namespace Karry.Modules.Trust.Application;

/// <summary>An integrity check as it was run: its report and who ran it (a user's subject; null for a system run).</summary>
public sealed record RecordedVerification(LedgerVerificationReport Report, string? VerifiedBy);

/// <summary>The current tenant's past integrity checks (row-level security scopes them to the tenant).</summary>
public interface IVerificationRecords
{
    Task RecordAsync(RecordedVerification verification, CancellationToken cancellationToken);

    Task<RecordedVerification?> LatestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The "Integrity check" of the portal (US-P1-AUD-03 AC2, ADR-0029 §2): runs <see cref="VerifyLedgerHandler"/> and
/// keeps its result, so the screen shows when the ledger was last checked, by whom, and what was found.
/// </summary>
public sealed class IntegrityChecks(VerifyLedgerHandler verify, IVerificationRecords records, ICallerAccess caller)
{
    public async Task<LedgerVerificationReport> RunAsync(CancellationToken cancellationToken)
    {
        var report = await verify.HandleAsync(cancellationToken).ConfigureAwait(false);
        await records.RecordAsync(new RecordedVerification(report, caller.Subject), cancellationToken).ConfigureAwait(false);
        return report;
    }

    public Task<RecordedVerification?> LatestAsync(CancellationToken cancellationToken) => records.LatestAsync(cancellationToken);
}
