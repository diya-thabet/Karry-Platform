using Karry.Modules.Sync.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Sync.Tests;

public sealed class InboxRulesTests
{
    private static readonly IReadOnlySet<CommandId> NothingStored = new HashSet<CommandId>();

    [Fact]
    public void Commands_in_the_next_sequence_numbers_are_accepted_and_advance_the_sequence()
    {
        var (first, second) = (Id(), Id());

        var (decisions, last) = InboxRules.Decide(4, NothingStored, [new(first, 5, true), new(second, 6, true)]);

        decisions.Should().Equal(new UploadDecision(first, UploadOutcome.Accepted, null), new UploadDecision(second, UploadOutcome.Accepted, null));
        last.Should().Be(6);
    }

    [Fact]
    public void A_command_already_stored_or_twice_in_the_batch_is_a_duplicate_and_does_not_move_the_sequence()
    {
        var (stored, fresh) = (Id(), Id());

        var (decisions, last) = InboxRules.Decide(1, new HashSet<CommandId> { stored }, [new(stored, 1, true), new(fresh, 2, true), new(fresh, 2, true)]);

        decisions.Select(d => d.Outcome).Should().Equal(UploadOutcome.Duplicate, UploadOutcome.Accepted, UploadOutcome.Duplicate);
        last.Should().Be(2);
    }

    [Fact]
    public void A_bad_signature_is_rejected_and_the_commands_after_it_are_gaps()
    {
        var (forged, next) = (Id(), Id());

        var (decisions, last) = InboxRules.Decide(0, NothingStored, [new(forged, 1, false), new(next, 2, true)]);

        decisions.Should().Equal(
            new UploadDecision(forged, UploadOutcome.Rejected, InboxRules.SignatureInvalid),
            new UploadDecision(next, UploadOutcome.Rejected, InboxRules.SequenceGap));
        last.Should().Be(0);
    }

    [Fact]
    public void A_new_command_must_take_the_next_number_not_a_used_one_nor_one_further()
    {
        var (decisions, last) = InboxRules.Decide(3, NothingStored, [new(Id(), 3, true), new(Id(), 5, true), new(Id(), 4, true)]);

        decisions.Select(d => d.Code).Should().Equal(InboxRules.SequenceReused, InboxRules.SequenceGap, null);
        last.Should().Be(4);
    }

    [Fact]
    public void A_rejected_command_sent_again_with_a_good_signature_is_accepted()
    {
        var id = Id();

        var (decisions, _) = InboxRules.Decide(0, NothingStored, [new(id, 1, false), new(id, 1, true)]);

        decisions.Select(d => d.Outcome).Should().Equal(UploadOutcome.Rejected, UploadOutcome.Accepted);
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
