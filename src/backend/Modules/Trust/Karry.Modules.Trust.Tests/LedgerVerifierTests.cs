using Karry.Modules.Trust.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Trust.Tests;

/// <summary>The chain verifier finds the exact first broken sequence for each kind of tampering (US-P1-AUD-03 core).</summary>
[Trait("Story", "US-P1-AUD-03")]
public sealed class LedgerVerifierTests
{
    private static readonly TenantId Tenant = LedgerHashTests.Tenant;

    [Fact]
    public void An_untouched_chain_is_valid_and_reports_its_head()
    {
        var chain = Chain(5);

        var result = LedgerVerifier.Verify(chain);

        result.IsValid.Should().BeTrue();
        result.EntriesChecked.Should().Be(5);
        result.HeadHash.Should().Be(chain[^1].EntryHash);
    }

    [Fact]
    public void An_empty_ledger_is_valid() => LedgerVerifier.Verify([]).Should().Be(ChainVerification.Valid(0, null));

    public static TheoryData<string, int, string> Tamperings => new()
    {
        { "payload", 3, "payload does not match its hash" },
        { "payload-hash", 3, "entry hash does not match its content" },
        { "previous-hash", 4, "previous hash does not link to the entry before" },
        { "entry-hash", 2, "entry hash does not match its content" },
        { "occurred-at", 5, "entry hash does not match its content" },
        { "event-type", 1, "entry hash does not match its content" },
        { "missing", 3, "sequence 3 is missing (found 4)" },
        { "non-canonical", 2, "payload does not match its hash" },
        { "not-json", 6, "payload is not canonical JSON" },
    };

    [Theory]
    [MemberData(nameof(Tamperings))]
    [Trait("TestCase", "TC-P1-AUD-003")]
    public void Each_kind_of_tampering_is_reported_at_its_exact_sequence(string tampering, int sequence, string problem)
    {
        var chain = Chain(6);
        var index = sequence - 1;
        var entry = chain[index];
        chain[index] = tampering switch
        {
            "payload" => entry with { Payload = entry.Payload.Replace("\"n\":3", "\"n\":30", StringComparison.Ordinal) },
            "payload-hash" => entry with { Payload = """{"n":33}""", PayloadHash = LedgerHash.OfPayload("""{"n":33}""") },
            "previous-hash" => entry with { PreviousHash = LedgerHash.Genesis },
            "entry-hash" => entry with { EntryHash = new string('f', 64) },
            "occurred-at" => entry with { OccurredAt = entry.OccurredAt.AddMinutes(-5) },
            "event-type" => entry with { EventType = "test.forged.v1" },
            "non-canonical" => entry with { Payload = """{ "n": 2 }""" },
            "not-json" => entry with { Payload = "n=6" },
            _ => entry,
        };
        if (tampering == "missing")
        {
            chain.RemoveAt(index);
        }

        var result = LedgerVerifier.Verify(chain);

        result.IsValid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(sequence, tampering);
        result.Problem.Should().StartWith(problem);
        result.EntriesChecked.Should().Be(sequence - 1, "entries before the break were verified");
    }

    [Fact]
    public async Task Verification_stops_reading_at_the_first_break()
    {
        // A chain of millions of rows must not be streamed to the end once it is known to be broken.
        var chain = Chain(4);
        chain[1] = chain[1] with { EntryHash = new string('f', 64) };

        LedgerVerifier.Verify(ExplodingAfter(chain, 2)).FirstBrokenSequence.Should().Be(2);
        (await LedgerVerifier.VerifyAsync(ExplodingAfterAsync(chain, 2), TestContext.Current.CancellationToken)).FirstBrokenSequence.Should().Be(2);
    }

    [Fact]
    public void A_broken_check_accepts_no_further_entries()
    {
        var chain = Chain(3);
        var check = new LedgerChainCheck();
        check.Add(chain[0] with { EntryHash = new string('f', 64) }).Should().BeFalse();

        check.Add(chain[1]).Should().BeFalse("once broken, the result is final");
        check.Result.FirstBrokenSequence.Should().Be(1);
    }

    [Fact]
    public async Task Null_input_is_refused()
    {
        ((Action)(() => LedgerVerifier.Verify(null!))).Should().Throw<ArgumentNullException>();
        await ((Func<Task>)(() => LedgerVerifier.VerifyAsync(null!, CancellationToken.None))).Should().ThrowAsync<ArgumentNullException>();
        ((Action)(() => new LedgerChainCheck().Add(null!))).Should().Throw<ArgumentNullException>();
    }

    private static IEnumerable<LedgerEntry> ExplodingAfter(List<LedgerEntry> chain, int count)
    {
        foreach (var entry in chain.Take(count))
        {
            yield return entry;
        }

        throw new InvalidOperationException("read past the first broken entry");
    }

    private static async IAsyncEnumerable<LedgerEntry> ExplodingAfterAsync(List<LedgerEntry> chain, int count)
    {
        foreach (var entry in ExplodingAfter(chain, count))
        {
            await Task.Yield();
            yield return entry;
        }
    }

    /// <summary>A valid chain built with the hash functions (their correctness is pinned by LedgerHashTests).</summary>
    internal static List<LedgerEntry> Chain(int length)
    {
        var entries = new List<LedgerEntry>();
        var previous = LedgerHash.Genesis;
        var at = LedgerHashTests.OccurredAt;
        for (var seq = 1; seq <= length; seq++)
        {
            var payload = $$"""{"n":{{seq}}}""";
            var payloadHash = LedgerHash.OfPayload(payload);
            var hash = LedgerHash.OfEntry(previous, Tenant, seq, "test.happened.v1", payloadHash, at);
            entries.Add(new LedgerEntry(Tenant, seq, Guid.CreateVersion7(), "test.happened.v1", LedgerHash.ToStoragePrecision(at), payload, payloadHash, previous, hash));
            previous = hash;
            at = at.AddSeconds(1);
        }

        return entries;
    }
}
