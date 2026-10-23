using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Domain;
using Karry.Modules.Trust.Infrastructure;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Trust.Tests;

/// <summary>Verification against anchors as a pure unit (in-memory chain and store): every branch of the comparison.</summary>
[Trait("Story", "US-P1-AUD-03")]
public sealed class VerifyLedgerHandlerTests : IDisposable
{
    private static readonly TenantId Tenant = LedgerHashTests.Tenant;

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly SoftwareAnchorSigner signer = new(ECDsa.Create(ECCurve.NamedCurves.nistP256));
    private readonly List<StoredAnchor> anchors = [];
    private List<LedgerEntry> chain = LedgerVerifierTests.Chain(5);

    public void Dispose() => signer.Dispose();

    [Fact]
    public async Task Without_anchor_storage_the_chain_is_verified_and_anchors_are_not_checked()
    {
        var report = await Handler(store: false, verifier: false).HandleAsync(TestContext.Current.CancellationToken);

        report.Should().BeEquivalentTo(new { Valid = true, EntriesChecked = 5L, AnchorsChecked = (int?)null, VerifiedAt = clock.GetUtcNow() });
        report.HeadHash.Should().Be(chain[^1].EntryHash);
    }

    [Fact]
    public async Task A_store_without_a_verifier_does_not_check_anchors()
    {
        Anchor(3);

        (await Handler(store: true, verifier: false).HandleAsync(TestContext.Current.CancellationToken)).AnchorsChecked.Should().BeNull();
    }

    [Fact]
    public async Task Matching_anchors_are_counted()
    {
        Anchor(2);
        Anchor(5);

        var report = await Handler().HandleAsync(TestContext.Current.CancellationToken);

        report.Valid.Should().BeTrue();
        report.AnchorsChecked.Should().Be(2);
    }

    [Fact]
    public async Task An_anchored_entry_that_differs_is_reported_with_the_entries_verified_before_it()
    {
        Anchor(3);
        RewriteFrom(3);

        var report = await Handler().HandleAsync(TestContext.Current.CancellationToken);

        report.Should().BeEquivalentTo(new { Valid = false, FirstBrokenSequence = 3L, EntriesChecked = 2L, AnchorsChecked = (int?)1 });
        report.Problem.Should().Contain("entry 3 differs from its WORM anchor");
    }

    [Fact]
    public async Task An_anchor_mismatch_before_a_later_chain_break_is_the_one_reported()
    {
        Anchor(2);
        RewriteFrom(2);
        chain[3] = chain[3] with { EntryHash = new string('f', 64) }; // and the chain also breaks at 4

        var report = await Handler().HandleAsync(TestContext.Current.CancellationToken);

        report.FirstBrokenSequence.Should().Be(2, "the earliest problem wins");
        report.Problem.Should().Contain("WORM anchor");
    }

    [Fact]
    public async Task A_chain_break_before_an_anchor_mismatch_is_the_one_reported()
    {
        Anchor(5);
        chain[1] = chain[1] with { EntryHash = new string('f', 64) };

        (await Handler().HandleAsync(TestContext.Current.CancellationToken)).FirstBrokenSequence.Should().Be(2);
    }

    [Fact]
    public async Task Entries_removed_after_the_latest_of_several_anchors_are_reported()
    {
        Anchor(2);
        Anchor(5);
        chain = chain.Take(3).ToList();

        var report = await Handler().HandleAsync(TestContext.Current.CancellationToken);

        report.Should().BeEquivalentTo(new { Valid = false, FirstBrokenSequence = 4L, EntriesChecked = 3L });
        report.Problem.Should().Be("the chain ends at 3 but sequence 5 was anchored (entries removed)");
    }

    [Theory]
    [InlineData("not an anchor")]
    [InlineData("{}")]
    public async Task A_stored_object_that_is_not_an_anchor_is_reported(string document)
    {
        anchors.Add(new StoredAnchor(document, 1));

        var report = await Handler().HandleAsync(TestContext.Current.CancellationToken);

        report.Valid.Should().BeFalse();
        report.Problem.Should().Be("an anchor object is not a valid anchor of this tenant");
    }

    [Fact]
    public async Task An_anchor_of_another_tenant_in_this_tenants_prefix_is_reported()
    {
        var foreign = new LedgerAnchor(new TenantId(Guid.CreateVersion7()), 1, chain[0].EntryHash, clock.GetUtcNow(), signer.KeyId);
        anchors.Add(new StoredAnchor(foreign.ToDocument(signer.Sign(foreign.SignedBytes())), 1));

        (await Handler().HandleAsync(TestContext.Current.CancellationToken)).Problem.Should().Contain("not a valid anchor of this tenant");
    }

    [Fact]
    [Trait("Story", "US-P1-AUD-02")]
    public void Anchoring_metrics_record_outcomes_and_move_the_last_success_only_when_every_tenant_succeeded()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var metrics = new AnchoringMetrics(factory);
        var outcomes = new List<string>();
        var instruments = new Dictionary<string, string?>();
        long? lastSuccess = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AnchoringMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    instruments[instrument.Name] = instrument.Unit;
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "karry.ledger.anchor.runs")
            {
                outcomes.Add((string)tags.ToArray().Single(t => t.Key == "outcome").Value!);
            }
            else if (instrument.Name == "karry.ledger.anchor.last_success")
            {
                lastSuccess = value;
            }
        });
        listener.Start();

        listener.RecordObservableInstruments();
        lastSuccess.Should().BeNull("no run yet: the gauge is absent, which the alert treats as stale");

        metrics.RunCompleted(true, clock.GetUtcNow());
        metrics.RunCompleted(false, clock.GetUtcNow().AddHours(1));
        listener.RecordObservableInstruments();

        outcomes.Should().Equal("success", "partial_failure");
        lastSuccess.Should().Be(clock.GetUtcNow().ToUnixTimeSeconds(), "a partial failure does not count as a success");
        ((Action)(() => _ = new AnchoringMetrics(null!))).Should().Throw<ArgumentNullException>();

        // The alert and dashboard query karry_ledger_anchor_last_success_seconds: Prometheus derives the suffix from the unit.
        instruments.Should().Equal(new Dictionary<string, string?>
        {
            ["karry.ledger.anchor.runs"] = "{run}",
            ["karry.ledger.anchor.last_success"] = "s",
        });
    }

    private VerifyLedgerHandler Handler(bool store = true, bool verifier = true)
    {
        var context = new TenantContext();
        context.Set(Tenant);
        return new VerifyLedgerHandler(new ListReader(chain), context, clock, store ? new ListStore(anchors) : null, verifier ? signer : null);
    }

    private void Anchor(int sequence)
    {
        var anchor = new LedgerAnchor(Tenant, sequence, chain[sequence - 1].EntryHash, clock.GetUtcNow(), signer.KeyId);
        anchors.Add(new StoredAnchor(anchor.ToDocument(signer.Sign(anchor.SignedBytes())), 1));
    }

    /// <summary>Consistent rewrite from <paramref name="sequence"/>: valid chain, different hashes than anchored.</summary>
    private void RewriteFrom(int sequence)
    {
        var previous = chain[sequence - 2 < 0 ? 0 : sequence - 2].EntryHash;
        if (sequence == 1)
        {
            previous = LedgerHash.Genesis;
        }

        for (var i = sequence - 1; i < chain.Count; i++)
        {
            var entry = chain[i];
            var payload = i == sequence - 1 ? """{"n":999}""" : entry.Payload;
            var payloadHash = LedgerHash.OfPayload(payload);
            var hash = LedgerHash.OfEntry(previous, Tenant, entry.Sequence, entry.EventType, payloadHash, entry.OccurredAt);
            chain[i] = entry with { Payload = payload, PayloadHash = payloadHash, PreviousHash = previous, EntryHash = hash };
            previous = hash;
        }
    }

    private sealed class ListReader(List<LedgerEntry> entries) : ILedgerReader
    {
        public async IAsyncEnumerable<LedgerEntry> ReadChainAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var entry in entries)
            {
                await Task.Yield();
                yield return entry;
            }
        }
    }

    private sealed class ListStore(List<StoredAnchor> anchors) : IAnchorStore
    {
        public Task PutAsync(LedgerAnchor anchor, string document, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<StoredAnchor> ListAsync(TenantId tenant, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var anchor in anchors)
            {
                await Task.Yield();
                yield return anchor;
            }
        }
    }
}
