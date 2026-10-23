using System.Runtime.CompilerServices;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Domain;

namespace Karry.Modules.Trust.Application;

/// <summary>Reads the current tenant's ledger in sequence order (row-level security scopes it to that tenant).</summary>
public interface ILedgerReader
{
    IAsyncEnumerable<LedgerEntry> ReadChainAsync(CancellationToken cancellationToken);
}

public sealed record LedgerVerificationReport(
    bool Valid, long EntriesChecked, long? FirstBrokenSequence, string? Problem, string? HeadHash,
    int? AnchorsChecked, DateTimeOffset VerifiedAt);

/// <summary>
/// Recomputes the current tenant's whole chain and compares it with its WORM anchors (US-P1-AUD-03 AC1):
/// payload hashes, links and entry hashes; then every anchor must carry a valid signature and the entry at its
/// sequence must have exactly the anchored hash. A consistently rewritten chain (all hashes recomputed by someone with
/// database access) passes the first check but not the second. Without anchor storage configured, anchors are not
/// checked (<c>AnchorsChecked</c> null).
/// </summary>
public sealed class VerifyLedgerHandler(ILedgerReader ledger, ITenantContext tenant, TimeProvider clock,
    IAnchorStore? anchors = null, IAnchorVerifier? signatures = null)
{
    public async Task<LedgerVerificationReport> HandleAsync(CancellationToken cancellationToken)
    {
        var expected = await LoadAnchorsAsync(cancellationToken).ConfigureAwait(false);
        if (expected is { Problem: { } anchorProblem })
        {
            return Report(ChainVerification.Broken(0, anchorProblem.Sequence, anchorProblem.Message), expected.Anchors.Count);
        }

        var anchoredHashes = expected?.Anchors.ToDictionary(a => a.Sequence, a => a.EntryHash) ?? [];
        string? mismatch = null;
        long mismatchAt = 0;
        var result = await LedgerVerifier.VerifyAsync(Observe(ledger.ReadChainAsync(cancellationToken), cancellationToken), cancellationToken).ConfigureAwait(false);

        if (mismatch is not null && (result.IsValid || result.FirstBrokenSequence > mismatchAt))
        {
            return Report(ChainVerification.Broken(mismatchAt - 1, mismatchAt, mismatch), anchoredHashes.Count);
        }

        if (result.IsValid && anchoredHashes.Count > 0 && anchoredHashes.Keys.Max() > result.EntriesChecked)
        {
            var missing = anchoredHashes.Keys.Max();
            return Report(ChainVerification.Broken(result.EntriesChecked, result.EntriesChecked + 1,
                $"the chain ends at {result.EntriesChecked} but sequence {missing} was anchored (entries removed)"), anchoredHashes.Count);
        }

        return Report(result, expected?.Anchors.Count);

        // Compares entries with anchored hashes while they stream through the chain check.
        async IAsyncEnumerable<LedgerEntry> Observe(IAsyncEnumerable<LedgerEntry> entries, [EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var entry in entries.WithCancellation(token).ConfigureAwait(false))
            {
                if (mismatch is null && anchoredHashes.TryGetValue(entry.Sequence, out var anchoredHash)
                    && !string.Equals(anchoredHash, entry.EntryHash, StringComparison.Ordinal))
                {
                    mismatch = $"entry {entry.Sequence} differs from its WORM anchor (the chain was rewritten)";
                    mismatchAt = entry.Sequence;
                }

                yield return entry;
            }
        }
    }

    private LedgerVerificationReport Report(ChainVerification result, int? anchorsChecked) =>
        new(result.IsValid, result.EntriesChecked, result.FirstBrokenSequence, result.Problem, result.HeadHash, anchorsChecked, clock.GetUtcNow());

    private sealed record AnchorProblem(long Sequence, string Message);

    private sealed record LoadedAnchors(List<LedgerAnchor> Anchors, AnchorProblem? Problem);

    private async Task<LoadedAnchors?> LoadAnchorsAsync(CancellationToken cancellationToken)
    {
        if (anchors is null || signatures is null || tenant.Current is not { } current)
        {
            return null;
        }

        var loaded = new List<LedgerAnchor>();
        await foreach (var stored in anchors.ListAsync(current, cancellationToken).ConfigureAwait(false))
        {
            if (LedgerAnchor.FromDocument(stored.Document) is not var (anchor, signature) || anchor.Tenant != current)
            {
                return new LoadedAnchors(loaded, new AnchorProblem(0, "an anchor object is not a valid anchor of this tenant"));
            }

            if (stored.Versions > 1)
            {
                return new LoadedAnchors(loaded, new AnchorProblem(anchor.Sequence, $"the anchor of sequence {anchor.Sequence} was overwritten ({stored.Versions} versions)"));
            }

            if (!signatures.Verify(anchor.KeyId, anchor.SignedBytes(), signature))
            {
                return new LoadedAnchors(loaded, new AnchorProblem(anchor.Sequence, $"the anchor of sequence {anchor.Sequence} has an invalid signature"));
            }

            loaded.Add(anchor);
        }

        return new LoadedAnchors(loaded, null);
    }
}
