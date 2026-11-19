using System.Buffers.Text;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Sync;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Contracts;
using Karry.Modules.FieldOps.Application;
using Karry.Modules.FieldOps.Domain;
using Karry.Modules.FieldOps.Infrastructure;
using Karry.Modules.Integrity.Infrastructure;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Trust.Contracts;
using Karry.Modules.Trust.Infrastructure;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>One PostgreSQL server for the class; each test gets its own migrated database.</summary>
public sealed class FieldOpsDatabase : IAsyncLifetime
{
    public PostgresServer Server { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Server = await PostgresServer.StartAsync();

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>
/// The start and end handlers of a shift entry (ADR-0034) on real PostgreSQL with the real roles, migrations and
/// envelope recorder: each signed fact is stored with its envelope in the upload's transaction, flagged when it needs
/// a look, and refused only for a structural problem.
/// </summary>
[Trait("Story", "US-P2-FO-01")]
public sealed class ShiftEntryHandlersTests(FieldOpsDatabase postgres) : IClassFixture<FieldOpsDatabase>, IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("01970000-0000-7000-8000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("01970000-0000-7000-8000-00000000000b");
    private static readonly Guid Site = Guid.Parse("01970000-0000-7000-8000-000000000c01");
    private static readonly Guid OtherSite = Guid.Parse("01970000-0000-7000-8000-000000000c02");
    private static readonly Guid Asset = Guid.Parse("01970000-0000-7000-8000-000000000a01");
    private static readonly Guid HourMeter = Guid.Parse("01970000-0000-7000-8000-000000000b01");
    private static readonly Guid Device = Guid.Parse("01970000-0000-7000-8000-000000000d01");
    private static readonly Guid Operator = Guid.Parse("01970000-0000-7000-8000-000000000501");
    private static readonly Guid NextOperator = Guid.Parse("01970000-0000-7000-8000-000000000502");
    private static readonly DateTimeOffset ShiftStart = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
    private static readonly ResolvedShift DayShift = new(new DateOnly(2026, 11, 9), "D", "Day", ShiftStart, ShiftStart.AddHours(8), "Etc/UTC");
    private static readonly string Photo = "sha256:" + new string('a', 64);

    private readonly Meters meters = new();
    private readonly KnownMeterEvents meterEvents = new();
    private readonly Shifts shifts = new();
    private readonly Devices devices = new();
    private KarryDatabase database = null!;
    private ServiceProvider services = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        database = await postgres.Server.NewKarryDatabaseAsync();
        await using (var migrations = new ServiceCollection().AddLogging().AddTrustInfrastructure(database.App).AddFieldOpsInfrastructure(database.App).AddIntegrityInfrastructure(database.App).BuildServiceProvider())
        {
            await MigrationRunner.MigrateAsync(migrations, database.Owner, CancellationToken.None);
        }

        services = new ServiceCollection().AddLogging()
            .AddTrustInfrastructure(database.App).AddFieldOpsInfrastructure(database.App).AddIntegrityInfrastructure(database.App)
            .AddSingleton<IDeviceDirectory>(devices).AddSingleton<IMeterDirectory>(meters).AddSingleton<IMeterEvents>(meterEvents).AddSingleton<IShiftCalendar>(shifts)
            .BuildServiceProvider(validateScopes: true);
        meters.All[HourMeter] = new MeterSummary(HourMeter, Asset, "EX-01", "hours", "ok", [new AssetSiteAssignment(Site, ShiftStart.AddDays(-30))]);
        shifts.Shift = DayShift;
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        devices.Dispose();
        NpgsqlConnection.ClearAllPools();
    }

    [Fact]
    public async Task A_start_then_an_end_store_two_versions_each_with_its_envelope_and_ledger_entry()
    {
        var root = Guid.CreateVersion7();

        var start = await HandleAsync(TenantA, StartShiftEntry.Name, Start(root, ShiftStart.AddMinutes(5), "12482.5", remarks: "cold start"));
        var end = await HandleAsync(TenantA, EndShiftEntry.Name, End(root, 2, ShiftStart.AddHours(8), "12490.0", stoppages: """[{"reason":"weather","minutes":30}]"""));

        start.Should().Be(new HandlerOutcome.Accepted(root));
        end.Should().Be(new HandlerOutcome.Accepted(root));
        var rows = await RowsAsync(TenantA, "SELECT version || '|' || status || '|' || operator_id || '|' || meter_kind || '|' || segment_no || '|' || shift_code || '|' || shift_date || '|' "
            + "|| start_value || '|' || coalesce(end_value::text, '-') || '|' || coalesce(remarks, '-') || '|' || stoppages::text || '|' || estimated FROM fieldops.shift_entries ORDER BY version");
        rows.Should().Equal(
            $"1|Open|{Operator}|hours|1|D|2026-11-09|12482.5|-|cold start|[]|false",
            $"2|Closed|{Operator}|hours|1|D|2026-11-09|12482.5|12490.0|cold start|[{{\"reason\": \"weather\", \"minutes\": 30}}]|false");
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.signature_envelopes")).Should().Be(2);
        (await ScalarAsync<long>(database.Admin, $"SELECT count(*) FROM audit.ledger_entries WHERE event_type = '{EnvelopeLedger.ReceivedEvent}'")).Should().Be(2);
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM fieldops.shift_entries e JOIN audit.signature_envelopes s ON s.envelope_id = e.envelope_id")).Should().Be(2);
    }

    [Fact]
    public async Task Where_the_tablet_was_when_the_start_and_the_end_were_signed_is_kept_on_the_entry()
    {
        var root = Guid.CreateVersion7();

        (await HandleAsync(TenantA, StartShiftEntry.Name, Signed(Record(root, ShiftStart, "10.0"), 1, geo: Geo(36.80451234, 10.0056, 12.2))))
            .Should().Be(new HandlerOutcome.Accepted(root));
        (await HandleAsync(TenantA, EndShiftEntry.Name, End(root, 2, ShiftStart.AddHours(8), "18.0", geo: Geo(-36.8, -10.5, 0))))
            .Should().Be(new HandlerOutcome.Accepted(root));

        (await RowsAsync(TenantA, "SELECT version || '|' || coalesce(start_latitude || ',' || start_longitude || ',' || start_accuracy_m, '-') || '|' "
            + "|| coalesce(end_latitude || ',' || end_longitude || ',' || end_accuracy_m, '-') FROM fieldops.shift_entries ORDER BY version"))
            .Should().Equal("1|36.804512,10.005600,13|-", "2|36.804512,10.005600,13|-36.800000,-10.500000,0");
    }

    [Fact]
    public async Task A_position_out_of_range_is_refused_with_its_envelope()
    {
        var outcome = await HandleAsync(TenantA, StartShiftEntry.Name, Signed(Record(Guid.CreateVersion7(), ShiftStart, "10.0"), 1, geo: Geo(91, 10, 5)));

        outcome.Should().Be(new HandlerOutcome.Rejected(EnvelopeCodes.Invalid, false));
        await NothingWrittenAsync();
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-005")]
    public async Task An_hour_meter_running_beyond_the_segment_going_backwards_or_ending_before_its_start_is_stored_and_flagged()
    {
        // 8 h of real time + 0.5 h tolerance: 8.5 h is fine, 8.6 h is not.
        (await StartAndEndAsync(ShiftStart, "100.0", ShiftStart.AddHours(8), "108.5")).Should().BeOfType<HandlerOutcome.Accepted>();
        (await StartAndEndAsync(ShiftStart, "100.0", ShiftStart.AddHours(8), "108.6")).Should().BeEquivalentTo(Flagged(ShiftEntryFlags.Delta));
        (await StartAndEndAsync(ShiftStart, "100.0", ShiftStart.AddHours(8), "99.9")).Should().BeEquivalentTo(Flagged(ShiftEntryFlags.Delta));
        (await StartAndEndAsync(ShiftStart, "100.0", ShiftStart, "100.0")).Should().BeEquivalentTo(Flagged(ShiftEntryFlags.EndBeforeStart));

        (await RowsAsync(TenantA, "SELECT array_to_string(flags, ',') FROM fieldops.shift_entries WHERE version = 2 ORDER BY end_value, ended_at"))
            .Should().Equal("RULE-FO-02", "END_BEFORE_START", "", "RULE-FO-02");
    }

    [Fact]
    public async Task A_kilometre_meter_is_not_held_to_the_hour_tolerance()
    {
        var kmMeter = Guid.CreateVersion7();
        meters.All[kmMeter] = new MeterSummary(kmMeter, Asset, "EX-01", "km", "ok", [new AssetSiteAssignment(Site, ShiftStart.AddDays(-1))]);
        var root = Guid.CreateVersion7();

        await HandleAsync(TenantA, StartShiftEntry.Name, Start(root, ShiftStart, "1000.0", meter: kmMeter));
        var end = await HandleAsync(TenantA, EndShiftEntry.Name, End(root, 2, ShiftStart.AddHours(8), "1240.0"));

        end.Should().Be(new HandlerOutcome.Accepted(root));
    }

    [Fact]
    public async Task A_start_in_no_shift_with_a_machine_on_another_site_is_stored_without_a_shift_and_flagged()
    {
        shifts.Shift = null;
        meters.All[HourMeter] = meters.All[HourMeter] with { Sites = [new AssetSiteAssignment(Site, ShiftStart.AddDays(-30)), new AssetSiteAssignment(OtherSite, ShiftStart.AddDays(-1))] };
        var root = Guid.CreateVersion7();

        var outcome = await HandleAsync(TenantA, StartShiftEntry.Name, Start(root, ShiftStart, "10.0"));

        outcome.Should().BeEquivalentTo(new HandlerOutcome.Conflict(root, [ShiftEntryFlags.AssetNotOnSite, ShiftEntryFlags.OutsidePattern]));
        (await RowsAsync(TenantA, "SELECT coalesce(shift_code, '-') || '|' || array_to_string(flags, ',') FROM fieldops.shift_entries"))
            .Should().Equal("-|ASSET_NOT_ON_SITE,SHIFT_OUTSIDE_PATTERN");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-010")]
    public async Task A_start_on_a_failed_meter_is_marked_estimated()
    {
        meters.All[HourMeter] = meters.All[HourMeter] with { Status = "failed" };

        await HandleAsync(TenantA, StartShiftEntry.Name, Start(Guid.CreateVersion7(), ShiftStart, "10.0"));

        (await RowsAsync(TenantA, "SELECT estimated::text FROM fieldops.shift_entries")).Should().Equal("true");
    }

    [Theory]
    [InlineData("startValue", "\"12482.55\"")]
    [InlineData("startValue", "12482.5")]
    [InlineData("startValue", "\"-1.0\"")]
    [InlineData("startValue", "\"10000000.0\"")]
    [InlineData("startValue", "\"01.0\"")]
    [InlineData("fuelStartPercent", "101")]
    [InlineData("startPhoto", "\"sha256:abc\"")]
    [InlineData("startedAt", "\"2026-11-09 06:00\"")]
    [InlineData("remarks", "\"   \"")]
    [InlineData("remarks", "null")]
    [InlineData("operatorId", "\"01970000-0000-7000-8000-000000000999\"")]
    [InlineData("version", "2")]
    public async Task A_start_record_of_the_wrong_shape_is_rejected_for_good_and_nothing_is_written(string field, string json)
    {
        var record = Record(Guid.CreateVersion7(), ShiftStart, "10.0");
        record[field] = JsonNode.Parse(json);

        var outcome = await HandleAsync(TenantA, StartShiftEntry.Name, Signed(record, 1));

        outcome.Should().Be(SignedRecord.Invalid);
        await NothingWrittenAsync();
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-002")]
    public async Task A_record_that_is_not_the_one_the_envelope_names_or_was_signed_for_is_rejected_and_nothing_is_written()
    {
        var root = Guid.CreateVersion7();
        var payload = Signed(Record(root, ShiftStart, "10.0"), 1);
        var changedRecord = payload.DeepClone().AsObject();
        changedRecord["record"]!["startValue"] = "11.0";
        var otherRecordId = Signed(Record(root, ShiftStart, "10.0"), 1);
        otherRecordId["envelope"]!["recordId"] = Guid.CreateVersion7().ToString();
        var badSignature = Signed(Record(Guid.CreateVersion7(), ShiftStart, "10.0"), 1);
        badSignature["envelope"]!["context"]!["appVersion"] = "9.9.9";
        var otherTablet = Signed(Record(Guid.CreateVersion7(), ShiftStart, "10.0"), 1);

        (await HandleAsync(TenantA, StartShiftEntry.Name, changedRecord)).Should().Be(SignedRecord.EnvelopeMismatch);
        (await HandleAsync(TenantA, StartShiftEntry.Name, otherRecordId)).Should().Be(SignedRecord.EnvelopeMismatch);
        (await HandleAsync(TenantA, StartShiftEntry.Name, badSignature)).Should().Be(new HandlerOutcome.Rejected(EnvelopeCodes.SignatureInvalid, false));
        (await HandleAsync(TenantA, StartShiftEntry.Name, otherTablet, device: Guid.CreateVersion7())).Should().Be(new HandlerOutcome.Rejected(EnvelopeCodes.DeviceMismatch, false));
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task A_meter_that_is_unknown_or_not_the_machines_is_rejected_for_good()
    {
        var otherAssetMeter = Guid.CreateVersion7();
        meters.All[otherAssetMeter] = meters.All[HourMeter] with { MeterId = otherAssetMeter, AssetId = Guid.CreateVersion7() };

        (await HandleAsync(TenantA, StartShiftEntry.Name, Start(Guid.CreateVersion7(), ShiftStart, "10.0", meter: Guid.CreateVersion7()))).Should().Be(StartShiftEntry.MeterUnknown);
        (await HandleAsync(TenantA, StartShiftEntry.Name, Start(Guid.CreateVersion7(), ShiftStart, "10.0", meter: otherAssetMeter))).Should().Be(StartShiftEntry.MeterUnknown);
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task An_end_before_its_start_is_retried_later_and_an_end_twice_or_a_second_start_is_rejected()
    {
        var root = Guid.CreateVersion7();

        (await HandleAsync(TenantA, EndShiftEntry.Name, End(root, 2, ShiftStart.AddHours(8), "18.0"))).Should().Be(EndShiftEntry.EntryUnknown);
        EndShiftEntry.EntryUnknown.Retryable.Should().BeTrue();
        (await HandleAsync(TenantA, StartShiftEntry.Name, Start(root, ShiftStart, "10.0"))).Should().Be(new HandlerOutcome.Accepted(root));
        (await HandleAsync(TenantA, StartShiftEntry.Name, Start(root, ShiftStart, "10.0"))).Should().Be(StartShiftEntry.EntryExists);
        (await HandleAsync(TenantA, EndShiftEntry.Name, End(root, 3, ShiftStart.AddHours(8), "18.0"))).Should().Be(EndShiftEntry.VersionConflict);
        (await HandleAsync(TenantA, EndShiftEntry.Name, End(root, 2, ShiftStart.AddHours(8), "18.0"))).Should().Be(new HandlerOutcome.Accepted(root));
        (await HandleAsync(TenantA, EndShiftEntry.Name, End(root, 3, ShiftStart.AddHours(8), "18.0"))).Should().Be(EndShiftEntry.VersionConflict);

        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM fieldops.shift_entries")).Should().Be(2);
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.signature_envelopes")).Should().Be(2);
    }

    [Fact]
    [Trait("Story", "US-P2-FO-05")]
    public async Task A_handover_start_waits_for_the_previous_segment_then_becomes_the_next_segment_signed_by_the_incoming_operator()
    {
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var handover = Start(second, ShiftStart.AddHours(4), "14.0", previous: first, signer: NextOperator);

        (await HandleAsync(TenantA, StartShiftEntry.Name, handover)).Should().Be(StartShiftEntry.PreviousUnknown);
        StartShiftEntry.PreviousUnknown.Retryable.Should().BeTrue();
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(first, ShiftStart, "10.0"));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(first, 2, ShiftStart.AddHours(4), "14.0"));
        (await HandleAsync(TenantA, StartShiftEntry.Name, handover)).Should().Be(new HandlerOutcome.Accepted(second));

        (await RowsAsync(TenantA, $"SELECT segment_no || '|' || operator_id || '|' || previous_root_id FROM fieldops.shift_entries WHERE root_id = '{second}'"))
            .Should().Equal($"2|{NextOperator}|{first}");
    }

    [Fact]
    [Trait("Story", "US-P2-FO-05")]
    [Trait("TestCase", "TC-P2-FO-006")]
    public async Task A_handover_start_that_is_not_the_previous_end_is_stored_and_flagged()
    {
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(first, ShiftStart, "10.0"));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(first, 2, ShiftStart.AddHours(4), "14.0"));

        var outcome = await HandleAsync(TenantA, StartShiftEntry.Name, Start(second, ShiftStart.AddHours(4), "14.5", previous: first, signer: NextOperator));

        outcome.Should().BeEquivalentTo(new HandlerOutcome.Conflict(second, [ShiftEntryFlags.Continuity]));
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-003")]
    [Trait("TestCase", "TC-P2-FO-007")]
    public async Task A_start_that_is_not_the_meters_last_end_is_stored_and_flagged_and_one_that_is_passes()
    {
        var (day, night, nextDay, early) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(day, ShiftStart, "100.0"));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(day, 2, ShiftStart.AddHours(8), "108.0"));

        var continues = await HandleAsync(TenantA, StartShiftEntry.Name, Start(night, ShiftStart.AddHours(8), "108.0", signer: NextOperator));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(night, 2, ShiftStart.AddHours(16), "116.0"));
        var lower = await HandleAsync(TenantA, StartShiftEntry.Name, Start(nextDay, ShiftStart.AddDays(1), "115.0"));
        var beforeAnyEnd = await HandleAsync(TenantA, StartShiftEntry.Name, Start(early, ShiftStart.AddDays(-1), "50.0", signer: Guid.CreateVersion7()));

        continues.Should().Be(new HandlerOutcome.Accepted(night));
        lower.Should().BeEquivalentTo(new HandlerOutcome.Conflict(nextDay, [ShiftEntryFlags.Continuity]), "the night shift ended at 116.0 (TC-P2-FO-007)");
        beforeAnyEnd.Should().Be(new HandlerOutcome.Accepted(early), "no end of the meter comes before it: nothing to compare with");

        var ended = await HandleAsync(TenantA, EndShiftEntry.Name, End(nextDay, 2, ShiftStart.AddDays(1).AddHours(8), "123.0"));
        ended.Should().Be(new HandlerOutcome.Accepted(nextDay), "the end raises nothing new");
        (await RowsAsync(TenantA, $"SELECT version || ':' || array_to_string(flags, ',') FROM fieldops.shift_entries WHERE root_id = '{nextDay}' ORDER BY version"))
            .Should().Equal("1:RULE-FO-01", "2:RULE-FO-01");
    }

    [Fact]
    public async Task A_start_after_a_segment_whose_end_never_came_is_not_compared()
    {
        var (day, forgotten, next) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(day, ShiftStart, "100.0"));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(day, 2, ShiftStart.AddHours(8), "108.0"));
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(forgotten, ShiftStart.AddHours(8), "108.0", signer: NextOperator));

        var outcome = await HandleAsync(TenantA, StartShiftEntry.Name, Start(next, ShiftStart.AddDays(1), "116.0", signer: Guid.CreateVersion7()));

        outcome.Should().Be(new HandlerOutcome.Accepted(next), "the last segment's end is missing (RULE-FO-08, P2.7), so 108.0 is not the value to continue");
    }

    [Fact]
    [Trait("Story", "US-P2-FO-05")]
    public async Task A_segment_whose_end_never_came_does_not_overlap_the_operators_later_shifts()
    {
        var (forgotten, inShift, today) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var otherMeter = Guid.CreateVersion7();
        meters.All[otherMeter] = meters.All[HourMeter] with { MeterId = otherMeter };
        shifts.Shift = null;
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(forgotten, ShiftStart.AddDays(-2), "10.0"));
        shifts.Shift = DayShift with { StartsAt = ShiftStart.AddDays(-1), EndsAt = ShiftStart.AddDays(-1).AddHours(8) };
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(inShift, ShiftStart.AddDays(-1), "500.0", meter: otherMeter));
        shifts.Shift = DayShift;

        var late = await HandleAsync(TenantA, StartShiftEntry.Name, Start(today, ShiftStart, "10.0"));

        late.Should().Be(new HandlerOutcome.Accepted(today),
            "a segment without an end runs to its shift's end, or 12 h without a shift; the missing end is RULE-FO-08's business (P2.7)");
    }

    [Fact]
    [Trait("Story", "US-P2-FO-05")]
    [Trait("TestCase", "TC-P2-FO-011")]
    public async Task An_operator_with_a_segment_still_running_on_another_machine_is_flagged_and_one_after_an_end_is_not()
    {
        var otherMeter = Guid.CreateVersion7();
        meters.All[otherMeter] = new MeterSummary(otherMeter, Guid.CreateVersion7(), "DUM-02", "hours", "ok", [new AssetSiteAssignment(Site, ShiftStart.AddDays(-30))]);
        var (excavator, truck, later) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(excavator, ShiftStart, "10.0"));

        var second = await HandleAsync(TenantA, StartShiftEntry.Name, Start(truck, ShiftStart.AddHours(1), "500.0", meter: otherMeter, asset: meters.All[otherMeter].AssetId));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(excavator, 2, ShiftStart.AddHours(2), "12.0"));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(truck, 2, ShiftStart.AddHours(3), "502.0"));
        var afterBoth = await HandleAsync(TenantA, StartShiftEntry.Name, Start(later, ShiftStart.AddHours(3), "12.0"));

        second.Should().BeEquivalentTo(new HandlerOutcome.Conflict(truck, [ShiftEntryFlags.Overlap]));
        afterBoth.Should().Be(new HandlerOutcome.Accepted(later), "both earlier segments ended by then");
        (await RowsAsync(TenantA, $"SELECT version || ':' || array_to_string(flags, ',') FROM fieldops.shift_entries WHERE root_id = '{truck}' ORDER BY version"))
            .Should().Equal("1:OVERLAP", "2:OVERLAP");
    }

    [Fact]
    [Trait("Story", "US-P2-FO-07")]
    [Trait("TestCase", "TC-P2-FO-007")]
    public async Task A_night_start_that_is_not_the_day_end_opens_an_integrity_flag_on_the_meter_linking_both_entries_and_photos()
    {
        var (meter, day, night) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        meters.All[meter] = meters.All[HourMeter] with { MeterId = meter };
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(day, ShiftStart, "100.0", meter: meter));
        var (dayEnd, nightStart) = ("sha256:" + new string('1', 64), "sha256:" + new string('2', 64));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(day, 2, ShiftStart.AddHours(8), "108.0", photo: dayEnd));

        await HandleAsync(TenantA, StartShiftEntry.Name, Start(night, ShiftStart.AddHours(12), "110.5", meter: meter, signer: NextOperator, photo: nightStart));

        (await FlagsAsync(TenantA)).Should().Equal(
            $"RULE-FO-01|high|meter|{meter}|{day}>{night}|{Site}|open|[{{\"id\": \"{day}\", \"type\": \"shift_entry\"}}, {{\"id\": \"{night}\", \"type\": \"shift_entry\"}}]"
            + $"|[\"{dayEnd}\", \"{nightStart}\"]|{{\"meterKind\": \"hours\", \"difference\": \"2.5\", \"foundValue\": \"110.5\", \"expectedValue\": \"108.0\"}}");
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.ledger_entries WHERE event_type = 'integrity.flag_opened.v1'")).Should().Be(1,
            "the continuity flag only: every photo here has its capture record and is used once");
        (await RowsAsync(TenantB, "SELECT key FROM integrity.flags")).Should().BeEmpty();
    }

    [Fact]
    [Trait("Story", "US-P2-FO-07")]
    [Trait("TestCase", "TC-P2-FO-008")]
    public async Task A_late_entry_is_compared_with_its_real_neighbours_and_closes_the_gap_it_explains()
    {
        var (meter, monday, tuesday, wednesday) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        meters.All[meter] = meters.All[HourMeter] with { MeterId = meter };
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(monday, ShiftStart, "100.0", meter: meter));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(monday, 2, ShiftStart.AddHours(8), "108.0"));
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(wednesday, ShiftStart.AddDays(2), "116.0", meter: meter, signer: NextOperator));
        (await FlagsAsync(TenantA, brief: true)).Should().Equal($"{monday}>{wednesday}|open");

        // Tuesday's entry was kept offline: it arrives last, starts where Monday ended and ends short of Wednesday's start.
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(tuesday, ShiftStart.AddDays(1), "108.0", meter: meter, signer: Guid.CreateVersion7()));
        (await FlagsAsync(TenantA, brief: true)).Should().Equal(
            [$"{monday}>{wednesday}|re-evaluated"], "Tuesday is still running: nothing to compare Wednesday with yet");
        await HandleAsync(TenantA, EndShiftEntry.Name, End(tuesday, 2, ShiftStart.AddDays(1).AddHours(8), "115.0"));

        (await FlagsAsync(TenantA, brief: true)).Should().Equal($"{monday}>{wednesday}|re-evaluated", $"{tuesday}>{wednesday}|open");
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.ledger_entries WHERE event_type = 'integrity.flag_closed.v1'")).Should().Be(1);
    }

    [Fact]
    [Trait("Story", "US-P2-AST-01")]
    [Trait("TestCase", "TC-P2-FO-009")]
    public async Task A_meter_replacement_recorded_after_the_entries_closes_the_gap_it_explains()
    {
        var (meter, day, night) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        meters.All[meter] = meters.All[HourMeter] with { MeterId = meter };
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(day, ShiftStart, "12480.0", meter: meter));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(day, 2, ShiftStart.AddHours(8), "12488.0"));
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(night, ShiftStart.AddHours(12), "3.0", meter: meter, signer: NextOperator));
        (await FlagsAsync(TenantA, brief: true)).Should().Equal($"{day}>{night}|open");

        // The manager records the replacement in Assets; its outbox hands it to FieldOps in the dispatcher's transaction.
        var replaced = new MeterEventSummary(Guid.CreateVersion7(), meter, "replaced", ShiftStart.AddHours(10), 12488.0m, 3.0m);
        meterEvents.All[meter] = [replaced];
        await InTransactionAsync(TenantA, (scope, connection, transaction) =>
            scope.GetServices<IMeterEventSubscriber>().Single().HandleAsync(replaced, connection, transaction, Ct));
        await InTransactionAsync(TenantA, (scope, connection, transaction) =>
            scope.GetServices<IMeterEventSubscriber>().Single().HandleAsync(replaced, connection, transaction, Ct));

        (await FlagsAsync(TenantA, brief: true)).Should().Equal([$"{day}>{night}|re-evaluated"], "the replacement explains the jump; handing it on twice changes nothing");
    }

    [Fact]
    [Trait("Story", "US-P2-INT-01")]
    [Trait("TestCase", "TC-P2-INT-005")]
    public async Task A_photo_without_its_capture_record_is_flagged_on_its_signed_fact_and_one_with_it_is_not()
    {
        var (bare, captured) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(bare, ShiftStart, "10.0", meter: FreshMeter(), signer: Guid.CreateVersion7(), captured: false));
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(captured, ShiftStart, "10.0", meter: FreshMeter(), signer: Guid.CreateVersion7()));

        (await RowsAsync(TenantA, "SELECT f.subject_type || '|' || e.root_id || '|' || f.key || '|' || f.severity FROM integrity.flags f "
            + "JOIN fieldops.shift_entries e ON e.envelope_id = f.subject_id WHERE f.rule = 'RULE-FO-05'"))
            .Should().Equal($"signature_envelope|{bare}|start|high");
    }

    [Fact]
    [Trait("Story", "US-P2-INT-01")]
    [Trait("TestCase", "TC-P2-INT-006")]
    public async Task A_photo_used_by_another_entry_or_for_both_ends_of_one_is_flagged_on_the_later_use()
    {
        var (first, second, same) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var shared = "sha256:" + new string('e', 64);
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(first, ShiftStart, "10.0", meter: FreshMeter(), signer: Guid.CreateVersion7(), photo: shared));
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(second, ShiftStart, "10.0", meter: FreshMeter(), signer: Guid.CreateVersion7(), photo: shared));
        var own = "sha256:" + new string('f', 64);
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(same, ShiftStart, "20.0", meter: FreshMeter(), signer: Guid.CreateVersion7(), photo: own));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(same, 2, ShiftStart.AddHours(8), "28.0", photo: own));

        (await RowsAsync(TenantA, "SELECT e.root_id || '|' || f.key || '|' || f.related::text || '|' || (f.details->>'sameAsStart') FROM integrity.flags f "
            + "JOIN fieldops.shift_entries e ON e.envelope_id = f.subject_id WHERE f.rule = 'RULE-FO-06' ORDER BY f.opened_at"))
            .Should().Equal(
                $"{second}|start|[{{\"id\": \"{second}\", \"type\": \"shift_entry\"}}, {{\"id\": \"{first}\", \"type\": \"shift_entry\"}}]|false",
                $"{same}|end|[{{\"id\": \"{same}\", \"type\": \"shift_entry\"}}]|true");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-005")]
    public async Task An_end_with_a_delta_out_of_bounds_opens_an_integrity_flag_on_the_entry_and_a_good_one_does_not()
    {
        var (bad, good) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(bad, ShiftStart, "100.0"));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(bad, 2, ShiftStart.AddHours(8), "99.0"));
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(good, ShiftStart.AddHours(9), "99.0", signer: NextOperator));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(good, 2, ShiftStart.AddHours(17), "107.0"));

        (await RowsAsync(TenantA, "SELECT rule || '|' || subject_type || '|' || subject_id || '|' || key || '|' || details::text FROM integrity.flags WHERE rule = 'RULE-FO-02'"))
            .Should().Equal($"RULE-FO-02|shift_entry|{bad}|delta|{{\"delta\": \"-1.0\", \"minutes\": \"480\", \"endValue\": \"99.0\", \"meterKind\": \"hours\", \"startValue\": \"100.0\"}}");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-007")]
    public async Task Entries_are_append_only_and_another_company_does_not_see_them()
    {
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(Guid.CreateVersion7(), ShiftStart, "10.0"));

        foreach (var sql in new[] { "UPDATE fieldops.shift_entries SET start_value = 11", "DELETE FROM fieldops.shift_entries", "TRUNCATE fieldops.shift_entries" })
        {
            await FluentActions.Awaiting(() => RowsAsync(TenantA, sql)).Should().ThrowAsync<PostgresException>(sql);
        }

        await FluentActions.Awaiting(() => ExecuteAsync(database.Owner, $"SELECT set_config('app.tenant_id', '{TenantA}', false); UPDATE fieldops.shift_entries SET start_value = 11"))
            .Should().ThrowAsync<PostgresException>("the trigger refuses even the owner");
        (await RowsAsync(TenantB, "SELECT root_id::text FROM fieldops.shift_entries")).Should().BeEmpty();
        (await RowsAsync(TenantA, "SELECT start_value::text FROM fieldops.shift_entries")).Should().Equal("10.0");
    }

    [Fact]
    public async Task A_command_may_carry_its_evidence_list_and_capture_records_and_nothing_else()
    {
        // As the field app sends it (signShiftEntry.ts): the sync module's evidence list and the photos' capture records.
        var withEvidence = Start(Guid.CreateVersion7(), ShiftStart, "10.0", meter: Guid.CreateVersion7(), signer: Guid.CreateVersion7());
        meters.All[Guid.Parse(withEvidence["record"]!["meterId"]!.GetValue<string>())] = meters.All[HourMeter] with { MeterId = Guid.Parse(withEvidence["record"]!["meterId"]!.GetValue<string>()) };
        withEvidence["evidence"] = JsonNode.Parse($$"""[{"sha256":"{{new string('a', 64)}}","size":10,"contentType":"image/webp"}]""");
        withEvidence["captures"] = JsonNode.Parse($$"""[{"sha256":"{{new string('a', 64)}}","capturedAt":"2026-11-09T06:00:00Z","deviceId":"d","appVersion":"1","width":1600,"height":1200}]""");
        var unknown = Start(Guid.CreateVersion7(), ShiftStart, "10.0", signer: Guid.CreateVersion7());
        unknown["note"] = "not read by anyone";
        var badCaptures = Start(Guid.CreateVersion7(), ShiftStart, "10.0", signer: Guid.CreateVersion7());
        badCaptures["captures"] = "one";

        (await HandleAsync(TenantA, StartShiftEntry.Name, withEvidence)).Should().BeOfType<HandlerOutcome.Accepted>();
        (await HandleAsync(TenantA, StartShiftEntry.Name, unknown)).Should().Be(SignedRecord.Invalid);
        (await HandleAsync(TenantA, StartShiftEntry.Name, badCaptures)).Should().Be(SignedRecord.Invalid);
    }
    [Fact]
    [Trait("Story", "US-P2-FO-04")]
    public async Task The_tablet_pulls_each_meters_last_end_and_its_running_segment_of_its_own_site_only()
    {
        var (first, second, elsewhere, otherCompany) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (siteMeter, awayMeter) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        meters.All[siteMeter] = meters.All[HourMeter] with { MeterId = siteMeter };
        meters.All[awayMeter] = meters.All[HourMeter] with { MeterId = awayMeter, Sites = [new AssetSiteAssignment(OtherSite, ShiftStart.AddDays(-30))] };
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(first, ShiftStart, "100.0", meter: siteMeter));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(first, 2, ShiftStart.AddHours(4), "104.0"));
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(second, ShiftStart.AddHours(4), "104.0", meter: siteMeter, previous: first, signer: NextOperator));
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(elsewhere, ShiftStart, "7.0", meter: awayMeter, signer: Guid.CreateVersion7()), site: OtherSite);
        await HandleAsync(TenantB, StartShiftEntry.Name, Start(otherCompany, ShiftStart, "1.0", meter: siteMeter, signer: Guid.CreateVersion7()));

        var delta = await MeterStatesAsync(TenantA, since: null);

        delta.Name.Should().Be("meterStates");
        delta.Removes.Should().BeEmpty();
        delta.Upserts.Should().BeEquivalentTo([new MeterStateSyncRow(
            siteMeter,
            new MeterEndRow(first, "104.0", ShiftStart.AddHours(4)),
            new OpenSegmentRow(second, 1, NextOperator, ShiftStart.AddHours(4), "104.0", DayShift.Date, DayShift.Code))]);
    }

    [Fact]
    [Trait("Story", "US-P2-FO-04")]
    public async Task A_segment_never_ended_is_not_running_once_a_later_one_started_and_a_delta_sends_only_meters_that_changed()
    {
        var (forgotten, later, quiet) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var (meter, quietMeter) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        meters.All[meter] = meters.All[HourMeter] with { MeterId = meter };
        meters.All[quietMeter] = meters.All[HourMeter] with { MeterId = quietMeter };
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(quiet, ShiftStart, "50.0", meter: quietMeter, signer: Guid.CreateVersion7()));
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(forgotten, ShiftStart, "10.0", meter: meter));
        var cursor = DateTimeOffset.UtcNow;
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(later, ShiftStart.AddHours(8), "18.0", meter: meter, signer: NextOperator));
        await HandleAsync(TenantA, EndShiftEntry.Name, End(later, 2, ShiftStart.AddHours(16), "26.0"));

        var delta = await MeterStatesAsync(TenantA, since: cursor);

        delta.Upserts.Should().BeEquivalentTo([new MeterStateSyncRow(meter, new MeterEndRow(later, "26.0", ShiftStart.AddHours(16)), null)],
            "the latest started segment ended; the forgotten one does not count as running any more, and the quiet meter did not change");
        (await MeterStatesAsync(TenantA, since: DateTimeOffset.UtcNow.AddMinutes(1))).Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_meter_whose_new_entries_are_on_another_site_is_removed_from_the_tablet()
    {
        var movedMeter = Guid.CreateVersion7();
        meters.All[movedMeter] = meters.All[HourMeter] with { MeterId = movedMeter, Sites = [new AssetSiteAssignment(OtherSite, ShiftStart.AddDays(-30))] };
        var cursor = DateTimeOffset.UtcNow;
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(Guid.CreateVersion7(), ShiftStart, "70.0", meter: movedMeter, signer: Guid.CreateVersion7()), site: OtherSite);

        var delta = await MeterStatesAsync(TenantA, since: cursor);

        delta.Upserts.Should().BeEmpty();
        delta.Removes.Should().Equal(movedMeter);
    }

    private static HandlerOutcome.Conflict Flagged(string flag) => new(null, [flag]);

    private static JsonObject Geo(double lat, double lon, double accuracy) => new() { ["lat"] = lat, ["lon"] = lon, ["acc"] = accuracy };

    private Guid FreshMeter()
    {
        var meter = Guid.CreateVersion7();
        meters.All[meter] = meters.All[HourMeter] with { MeterId = meter };
        return meter;
    }

    /// <summary>One segment on a fresh meter by a fresh operator, so continuity and overlaps stay out of the way.</summary>
    private async Task<HandlerOutcome> StartAndEndAsync(DateTimeOffset startedAt, string startValue, DateTimeOffset endedAt, string endValue)
    {
        var (root, meter) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        meters.All[meter] = meters.All[HourMeter] with { MeterId = meter };
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(root, startedAt, startValue, meter: meter, signer: Guid.CreateVersion7()));
        return await HandleAsync(TenantA, EndShiftEntry.Name, End(root, 2, endedAt, endValue)) switch
        {
            HandlerOutcome.Conflict conflict => conflict with { RecordId = null },
            var other => other,
        };
    }

    private static JsonObject Record(Guid root, DateTimeOffset startedAt, string startValue, Guid? meter = null, Guid? asset = null) => new()
    {
        ["rootId"] = root.ToString(),
        ["version"] = 1,
        ["assetId"] = (asset ?? Asset).ToString(),
        ["meterId"] = (meter ?? HourMeter).ToString(),
        ["startedAt"] = startedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        ["startValue"] = startValue,
        ["fuelStartPercent"] = 75,
        ["startPhoto"] = Photo,
    };

    private JsonObject Start(
        Guid root, DateTimeOffset startedAt, string startValue, Guid? meter = null, Guid? previous = null, string? remarks = null, Guid? signer = null, Guid? asset = null,
        string? photo = null, bool captured = true)
    {
        var record = Record(root, startedAt, startValue, meter, asset);
        record["startPhoto"] = photo ?? NextPhoto();
        if (previous is { } id)
        {
            record["previousRootId"] = id.ToString();
        }

        if (remarks is not null)
        {
            record["remarks"] = remarks;
        }

        return Captured(Signed(record, 1, signer), record["startPhoto"]!.GetValue<string>(), captured);
    }

    private static int photos;

    /// <summary>A photo no other entry of the test uses (the photo rules, RULE-FO-05/06, stay out of the way).</summary>
    private static string NextPhoto() => "sha256:" + Interlocked.Increment(ref photos).ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The capture record of the command's photo, as the tablet sends it beside the signed record (ADR-0032 §5).</summary>
    private static JsonObject Captured(JsonObject payload, string photo, bool captured)
    {
        if (captured)
        {
            payload["captures"] = new JsonArray(new JsonObject
            {
                ["sha256"] = photo["sha256:".Length..],
                ["capturedAt"] = "2026-11-09T06:00:00Z",
                ["deviceId"] = "d",
                ["appVersion"] = "1",
                ["width"] = 1600,
                ["height"] = 1200,
            });
        }

        return payload;
    }

    private JsonObject End(Guid root, int version, DateTimeOffset endedAt, string endValue, string? stoppages = null, JsonObject? geo = null, string? photo = null, bool captured = true)
    {
        var endPhoto = photo ?? NextPhoto();
        var record = new JsonObject
        {
            ["rootId"] = root.ToString(),
            ["version"] = version,
            ["endedAt"] = endedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["endValue"] = endValue,
            ["fuelEndPercent"] = 40,
            ["endPhoto"] = endPhoto,
        };
        if (stoppages is not null)
        {
            record["stoppages"] = JsonNode.Parse(stoppages);
        }

        return Captured(Signed(record, version, geo: geo), endPhoto, captured);
    }

    /// <summary>The command payload: the record, an envelope naming it, and the tablet's signature over the envelope.</summary>
    private JsonObject Signed(JsonObject record, int version, Guid? signer = null, JsonObject? geo = null)
    {
        using var recordDocument = JsonDocument.Parse(record.ToJsonString());
        var envelope = new JsonObject
        {
            ["v"] = 1,
            ["envelopeId"] = Guid.CreateVersion7().ToString(),
            ["recordType"] = ShiftEntryRules.RecordType,
            ["recordId"] = record["rootId"]!.GetValue<string>(),
            ["recordVersion"] = version,
            ["payloadHash"] = SignatureEnvelopes.PayloadHash(recordDocument.RootElement),
            ["signer"] = new JsonObject { ["userId"] = (signer ?? Operator).ToString(), ["role"] = "operator", ["deviceId"] = Device.ToString() },
            ["context"] = new JsonObject { ["deviceTime"] = "2026-11-09T06:02:11.250+00:00", ["appVersion"] = "1.4.0" },
        };
        if (geo is not null)
        {
            envelope["context"]!["geo"] = geo;
        }

        return new JsonObject { ["record"] = record, ["envelope"] = envelope, ["deviceSignature"] = devices.Sign(envelope) };
    }

    /// <summary>Runs a handler as the Sync module does: in the tenant, joined to the upload's transaction, rolled back when rejected.</summary>
    private async Task<HandlerOutcome> HandleAsync(Guid tenant, string type, JsonObject payload, Guid? device = null, Guid? site = null)
    {
        // The signature covers the envelope as built; a test that changes it afterwards gets a bad signature.
        using var document = JsonDocument.Parse(payload.ToJsonString());
        await using var connection = new NpgsqlConnection(database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var handlerType = scope.ServiceProvider.GetRequiredService<SyncCommandTypes>().HandlerOf(type)!;
        var handler = (ISyncCommandHandler)scope.ServiceProvider.GetRequiredService(handlerType);
        var now = DateTimeOffset.UtcNow;
        var command = new VerifiedCommand(Guid.CreateVersion7(), device ?? Device, site ?? Site, 1, type, document.RootElement.Clone(), now, now, 0, payload.ToJsonString());

        var outcome = await handler.HandleAsync(command, new Joined(connection, transaction), Ct);
        if (outcome is HandlerOutcome.Rejected)
        {
            await transaction.RollbackAsync(Ct);
        }
        else
        {
            await transaction.CommitAsync(Ct);
        }

        return outcome;
    }

    /// <summary>The <c>meterStates</c> part of a pull by this site's tablet, as the Sync module asks for it (in the tenant).</summary>
    private async Task<SyncDelta> MeterStatesAsync(Guid tenant, DateTimeOffset? since)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        var deltas = new List<SyncDelta>();
        foreach (var source in scope.ServiceProvider.GetServices<ISyncSource>())
        {
            deltas.AddRange(await source.ChangesAsync(new SyncScope(Device, Site), since, Ct));
        }

        return deltas.Single(d => d.Name == "meterStates");
    }

    /// <summary>Runs work as the outbox dispatcher does: in the tenant, in a transaction committed at the end.</summary>
    private async Task InTransactionAsync(Guid tenant, Func<IServiceProvider, NpgsqlConnection, NpgsqlTransaction, Task> work)
    {
        await using var connection = new NpgsqlConnection(database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        await work(scope.ServiceProvider, connection, transaction);
        await transaction.CommitAsync(Ct);
    }

    /// <summary>The company's integrity flags, oldest first: in full, or as key and state (open, or the reason it closed).</summary>
    private Task<List<string>> FlagsAsync(Guid tenant, bool brief = false) => RowsAsync(tenant, brief
        ? "SELECT key || '|' || coalesce(close_reason, 'open') FROM integrity.flags WHERE rule = 'RULE-FO-01' ORDER BY opened_at, key"
        : "SELECT rule || '|' || severity || '|' || subject_type || '|' || subject_id || '|' || key || '|' || site_id || '|' || coalesce(close_reason, 'open') || '|' "
            + "|| related::text || '|' || evidence::text || '|' || details::text FROM integrity.flags WHERE rule = 'RULE-FO-01' ORDER BY opened_at, key");

    private async Task NothingWrittenAsync() =>
        (await ScalarAsync<long>(database.Admin, "SELECT (SELECT count(*) FROM fieldops.shift_entries) + (SELECT count(*) FROM audit.signature_envelopes) + (SELECT count(*) FROM audit.ledger_entries)"))
            .Should().Be(0);

    private async Task<List<string>> RowsAsync(Guid tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        var rows = new List<string>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                rows.Add(reader.GetString(0));
            }
        }

        await transaction.CommitAsync(Ct);
        return rows;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed record Joined(DbConnection Connection, DbTransaction Transaction) : ISyncTransaction;

    private sealed class Meters : IMeterDirectory
    {
        public Dictionary<Guid, MeterSummary> All { get; } = [];

        public Task<MeterSummary?> FindAsync(Guid meterId, CancellationToken cancellationToken) => Task.FromResult(All.GetValueOrDefault(meterId));
    }

    private sealed class Shifts : IShiftCalendar
    {
        public ResolvedShift? Shift { get; set; }

        public Task<ResolvedShift?> ResolveAsync(Guid siteId, DateTimeOffset instant, CancellationToken cancellationToken) =>
            Task.FromResult(siteId == Site ? Shift : null);
    }

    /// <summary>The tablet's key (Identity's device directory in the host).</summary>
    private sealed class Devices : IDeviceDirectory, IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string Sign(JsonObject envelope)
        {
            using var document = JsonDocument.Parse(envelope.ToJsonString());
            return Base64Url.EncodeToString(key.SignData(SignatureEnvelopes.Canonical(document.RootElement), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        public Task<DeviceKey?> FindAsync(Guid deviceId, CancellationToken cancellationToken)
        {
            var parameters = key.ExportParameters(false);
            return Task.FromResult(deviceId == Device ? new DeviceKey(deviceId, Guid.Empty, parameters.Q.X!, parameters.Q.Y!, false) : null);
        }

        public Task<bool> RememberAssertionAsync(Guid deviceId, string assertionId, DateTimeOffset expiresAt, CancellationToken cancellationToken) => Task.FromResult(true);

        public void Dispose() => key.Dispose();
    }
}
