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

    public static TheoryData<string, int> Tamperings => new()
    {
        { "payload", 3 }, { "payload-hash", 3 }, { "previous-hash", 4 }, { "entry-hash", 2 }, { "occurred-at", 5 },
        { "event-type", 1 }, { "missing", 3 }, { "non-canonical", 2 },
    };

    [Theory]
    [MemberData(nameof(Tamperings))]
    [Trait("TestCase", "TC-P1-AUD-003")]
    public void Each_kind_of_tampering_is_reported_at_its_exact_sequence(string tampering, int sequence)
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
            _ => entry,
        };
        if (tampering == "missing")
        {
            chain.RemoveAt(index);
        }

        var result = LedgerVerifier.Verify(chain);

        result.IsValid.Should().BeFalse();
        result.FirstBrokenSequence.Should().Be(sequence, tampering);
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
