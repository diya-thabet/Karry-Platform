using Karry.Modules.Sync.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Sync.Tests;

public sealed class InboxRulesTests
{
    private static readonly IReadOnlySet<CommandId> NothingStored = new HashSet<CommandId>();

    private static readonly UploadCandidate[] NothingWaiting = [];

    /// <summary>Every command that reaches its handler is accepted; <paramref name="handled"/> collects them in order.</summary>
    private static Task<UploadPlan> DecideAsync(long last, IReadOnlySet<CommandId> stored, UploadCandidate[] commands, UploadCandidate[]? waiting = null, List<UploadCandidate>? handled = null) =>
        InboxRules.DecideAsync(last, stored, waiting ?? NothingWaiting, commands, c =>
        {
            handled?.Add(c);
            return Task.FromResult(UploadDecision.Accepted(c.Id));
        });

    [Fact]
    public async Task Only_a_command_its_handler_stored_advances_the_sequence()
    {
        var (flagged, refused, after) = (Id(), Id(), Id());

        var plan = await InboxRules.DecideAsync(0, NothingStored, NothingWaiting, [new(flagged, 1, true), new(refused, 2, true), new(after, 3, true)],
            c => Task.FromResult(c.Id == flagged
                ? new UploadDecision(c.Id, UploadOutcome.Conflict, null, Flags: ["test.flag"])
                : UploadDecision.Rejected(c.Id, InboxRules.HandlerFailed, retryable: true)));

        plan.Decisions.Select(d => (d.Outcome, d.Code)).Should().Equal(
            (UploadOutcome.Conflict, null), (UploadOutcome.Rejected, InboxRules.HandlerFailed), (UploadOutcome.Waiting, null));
        plan.LastSequence.Should().Be(1, "the refused command keeps its number free, so the one after it waits");
        plan.Held.Select(h => h.Id).Should().Equal(after);
        plan.MissingSequences.Should().Equal(2);
    }

    [Fact]
    public async Task A_final_rejection_kept_by_its_handler_takes_its_number_and_the_commands_after_it_go_on()
    {
        var (refused, after, waiting) = (Id(), Id(), Id());

        var plan = await InboxRules.DecideAsync(0, NothingStored, [new(waiting, 4, true)], [new(refused, 1, true), new(after, 2, true), new(Id(), 3, true)],
            c => Task.FromResult(c.Id == refused ? UploadDecision.RejectedAndKept(c.Id, "sync.blob_mismatch") : UploadDecision.Accepted(c.Id)));

        plan.Decisions[0].Should().Be(UploadDecision.RejectedAndKept(refused, "sync.blob_mismatch"));
        plan.Decisions[0].Retryable.Should().BeFalse();
        plan.Decisions.Skip(1).Select(d => d.Outcome).Should().Equal(UploadOutcome.Accepted, UploadOutcome.Accepted);
        plan.LastSequence.Should().Be(4, "the kept rejection took number 1, so 2 and 3 followed and the waiting 4 was handled");
        plan.Promoted.Select(p => p.Id).Should().Equal(waiting);
    }

    [Fact]
    public async Task Commands_in_the_next_sequence_numbers_are_accepted_and_advance_the_sequence()
    {
        var (first, second) = (Id(), Id());

        var plan = await DecideAsync(4, NothingStored, [new(first, 5, true), new(second, 6, true)]);

        plan.Decisions.Should().Equal(new UploadDecision(first, UploadOutcome.Accepted, null), new UploadDecision(second, UploadOutcome.Accepted, null));
        plan.LastSequence.Should().Be(6);
        plan.MissingSequences.Should().BeEmpty();
    }

    [Fact]
    public async Task A_command_already_stored_or_twice_in_the_batch_is_a_duplicate_and_does_not_move_the_sequence()
    {
        var (stored, fresh) = (Id(), Id());

        var plan = await DecideAsync(1, new HashSet<CommandId> { stored }, [new(stored, 1, true), new(fresh, 2, true), new(fresh, 2, true)]);

        plan.Decisions.Select(d => d.Outcome).Should().Equal(UploadOutcome.Duplicate, UploadOutcome.Accepted, UploadOutcome.Duplicate);
        plan.LastSequence.Should().Be(2);
    }

    [Fact]
    public async Task A_bad_signature_is_rejected_and_the_commands_after_it_wait()
    {
        var (forged, next) = (Id(), Id());

        var plan = await DecideAsync(0, NothingStored, [new(forged, 1, false), new(next, 2, true)]);

        plan.Decisions.Should().Equal(
            UploadDecision.Rejected(forged, InboxRules.SignatureInvalid, retryable: false),
            new UploadDecision(next, UploadOutcome.Waiting, null));
        plan.LastSequence.Should().Be(0);
        plan.MissingSequences.Should().Equal(1);
    }

    [Fact]
    public async Task A_new_command_must_not_take_a_used_number_and_one_further_on_waits_for_the_gap()
    {
        var (used, ahead, next) = (Id(), Id(), Id());
        var handled = new List<UploadCandidate>();

        var plan = await DecideAsync(3, NothingStored, [new(used, 3, true), new(ahead, 5, true), new(next, 4, true)], handled: handled);

        plan.Decisions.Select(d => (d.Outcome, d.Code)).Should().Equal(
            (UploadOutcome.Rejected, InboxRules.SequenceReused), (UploadOutcome.Accepted, null), (UploadOutcome.Accepted, null));
        handled.Select(c => c.Id).Should().Equal([next, ahead], "the waiting command is handled right after the gap closes");
        plan.LastSequence.Should().Be(5);
        plan.Held.Should().BeEmpty("nothing is left waiting");
    }

    [Fact]
    public async Task A_rejected_command_sent_again_with_a_good_signature_is_accepted()
    {
        var id = Id();

        var plan = await DecideAsync(0, NothingStored, [new(id, 1, false), new(id, 1, true)]);

        plan.Decisions.Select(d => d.Outcome).Should().Equal(UploadOutcome.Rejected, UploadOutcome.Accepted);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SYNC-003")]
    public async Task Commands_waiting_from_an_earlier_upload_are_handled_in_order_when_the_gap_closes()
    {
        var (second, third, first) = (Id(), Id(), Id());
        var handled = new List<UploadCandidate>();

        var plan = await DecideAsync(0, NothingStored, [new(first, 1, true)], waiting: [new(third, 3, true), new(second, 2, true)], handled: handled);

        handled.Select(c => c.Id).Should().Equal(first, second, third);
        plan.Decisions.Should().Equal(UploadDecision.Accepted(first));
        plan.Promoted.Should().Equal(UploadDecision.Accepted(second), UploadDecision.Accepted(third));
        plan.LastSequence.Should().Be(3);
        plan.Held.Should().BeEmpty("they were kept by the earlier upload");
    }

    [Fact]
    public async Task The_missing_numbers_are_listed_and_a_waiting_command_sent_again_still_waits()
    {
        var (four, seven) = (Id(), Id());

        var plan = await DecideAsync(1, NothingStored, [new(seven, 7, true), new(four, 4, true)], waiting: [new(four, 4, true)]);

        plan.Decisions.Select(d => d.Outcome).Should().Equal(UploadOutcome.Waiting, UploadOutcome.Waiting);
        plan.MissingSequences.Should().Equal(2, 3, 5, 6);
        plan.Held.Select(h => h.Id).Should().Equal(seven);
    }

    [Fact]
    public async Task A_waiting_command_sent_again_before_the_gap_closes_gets_its_final_answer()
    {
        var (second, first) = (Id(), Id());

        var plan = await DecideAsync(0, NothingStored, [new(second, 2, true), new(first, 1, true)], waiting: [new(second, 2, true)]);

        plan.Decisions.Should().Equal(UploadDecision.Accepted(second), UploadDecision.Accepted(first));
        plan.Promoted.Should().Equal(UploadDecision.Accepted(second));
    }

    [Fact]
    public async Task Another_command_with_the_number_of_a_waiting_one_is_a_reused_number()
    {
        var plan = await DecideAsync(0, NothingStored, [new(Id(), 3, true)], waiting: [new(Id(), 3, true)]);

        plan.Decisions.Single().Code.Should().Be(InboxRules.SequenceReused);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SYNC-003")]
    public async Task A_device_cannot_keep_more_than_500_commands_waiting()
    {
        UploadCandidate[] waiting = [.. Enumerable.Range(0, InboxRules.MaxWaiting).Select(n => new UploadCandidate(Id(), 10 + (2 * n), true))]; // every other number missing

        var plan = await DecideAsync(0, NothingStored, [new(Id(), 5_000, true)], waiting: waiting);

        plan.Decisions.Single().Should().BeEquivalentTo(new { Outcome = UploadOutcome.Rejected, Code = InboxRules.TooFarAhead, Retryable = true });
        plan.MissingSequences.Should().HaveCount(InboxRules.MaxMissingListed, "an answer lists at most 100 numbers");
    }

    [Fact]
    public async Task A_waiting_command_of_the_batch_that_its_handler_refuses_is_not_kept()
    {
        var (ahead, next) = (Id(), Id());

        var plan = await InboxRules.DecideAsync(0, NothingStored, NothingWaiting, [new(ahead, 2, true), new(next, 1, true)],
            c => Task.FromResult(c.Id == ahead ? UploadDecision.Rejected(c.Id, InboxRules.HandlerFailed, retryable: true) : UploadDecision.Accepted(c.Id)));

        plan.Decisions.Select(d => d.Code).Should().Equal(InboxRules.HandlerFailed, null);
        plan.Held.Should().BeEmpty("the tablet keeps it and sends it again");
        plan.LastSequence.Should().Be(1);
    }

    [Theory]
    [InlineData("production.trip_recorded.v1", true)]
    [InlineData("maintenance.meter_read.v12", true)]
    [InlineData("production.trip_recorded", false)]
    [InlineData("trip.v1", false)]
    [InlineData("Production.trip.v1", false)]
    [InlineData("production.trip.v0", false)]
    [InlineData("production..trip.v1", false)]
    [InlineData(null, false)]
    public void A_command_type_is_a_versioned_dotted_name(string? type, bool valid)
    {
        InboxRules.IsCommandType(type).Should().Be(valid);
    }

    [Fact]
    public void A_command_type_has_at_most_100_characters()
    {
        InboxRules.IsCommandType($"a.{new string('b', 96)}.v1").Should().BeFalse();
    }

    [Fact]
    public void A_command_id_is_a_lowercase_uuid_version_7()
    {
        var id = Guid.CreateVersion7();

        InboxRules.TryParseCommandId(id.ToString("D"), out var parsed).Should().BeTrue();
        parsed.Value.Should().Be(id);
        InboxRules.TryParseCommandId(id.ToString("D").ToUpperInvariant(), out _).Should().BeFalse();
        InboxRules.TryParseCommandId(Guid.NewGuid().ToString("D"), out _).Should().BeFalse();
        InboxRules.TryParseCommandId(id.ToString("N"), out _).Should().BeFalse();
        InboxRules.TryParseCommandId(null, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("2026-10-28T09:15:00.123Z", true)]
    [InlineData("2026-10-28T10:15:00+01:00", true)]
    [InlineData("2026-10-28T09:15:00", false)]
    [InlineData("2026-10-28", false)]
    [InlineData("2026-13-28T09:15:00Z", false)]
    [InlineData(null, false)]
    public void Device_time_is_iso_8601_with_an_offset(string? text, bool valid)
    {
        InboxRules.TryParseDeviceTime(text, out _).Should().Be(valid);
    }

    [Fact]
    public void Device_time_keeps_the_instant()
    {
        InboxRules.TryParseDeviceTime("2026-10-28T10:15:00+01:00", out var at).Should().BeTrue();

        at.UtcDateTime.Should().Be(new DateTime(2026, 10, 28, 9, 15, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_stored_command_keeps_what_the_tablet_signed_and_its_status()
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        var device = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;
        var signed = new SignedCommand(Id(), 7, "test.item_recorded.v1", at.AddMinutes(-3), "{\"a\":1}", "sig");

        var received = InboxCommand.Receive(tenant, device, signed, at);
        var quarantined = InboxCommand.Quarantine(tenant, device, signed, at);

        received.Should().BeEquivalentTo(new
        {
            signed.Id,
            TenantId = tenant,
            DeviceId = device,
            signed.Sequence,
            signed.Type,
            signed.DeviceTime,
            signed.Body,
            signed.Signature,
            Status = InboxStatus.Received,
            ReceivedAt = at,
        });
        quarantined.Status.Should().Be(InboxStatus.Quarantined);
    }

    private static CommandId Id() => new(Guid.CreateVersion7());
}
