using Karry.Modules.Trust.Domain;

namespace Karry.Modules.Trust.Application;

/// <summary>Reads the current tenant's ledger in sequence order (row-level security scopes it to that tenant).</summary>
public interface ILedgerReader
{
    IAsyncEnumerable<LedgerEntry> ReadChainAsync(CancellationToken cancellationToken);
}

public sealed record LedgerVerificationReport(
    bool Valid, long EntriesChecked, long? FirstBrokenSequence, string? Problem, string? HeadHash, DateTimeOffset VerifiedAt);

/// <summary>
/// Recomputes the current tenant's whole chain (US-P1-AUD-03 AC1): payload hashes, links and entry hashes; reports
/// OK or the first broken sequence. Comparison with the WORM anchors is added with anchoring (P1.6 slice 4).
/// </summary>
public sealed class VerifyLedgerHandler(ILedgerReader ledger, TimeProvider clock)
{
    public async Task<LedgerVerificationReport> HandleAsync(CancellationToken cancellationToken)
    {
        var result = await LedgerVerifier.VerifyAsync(ledger.ReadChainAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return new LedgerVerificationReport(result.IsValid, result.EntriesChecked, result.FirstBrokenSequence, result.Problem,
            result.HeadHash, clock.GetUtcNow());
    }
}
