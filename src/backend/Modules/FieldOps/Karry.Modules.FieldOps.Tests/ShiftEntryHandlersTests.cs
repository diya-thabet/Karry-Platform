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
    private readonly Shifts shifts = new();
    private readonly Devices devices = new();
    private KarryDatabase database = null!;
    private ServiceProvider services = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        database = await postgres.Server.NewKarryDatabaseAsync();
        await using (var migrations = new ServiceCollection().AddLogging().AddTrustInfrastructure(database.App).AddFieldOpsInfrastructure(database.App).BuildServiceProvider())
        {
            await MigrationRunner.MigrateAsync(migrations, database.Owner, CancellationToken.None);
        }

        services = new ServiceCollection().AddLogging()
            .AddTrustInfrastructure(database.App).AddFieldOpsInfrastructure(database.App)
            .AddSingleton<IDeviceDirectory>(devices).AddSingleton<IMeterDirectory>(meters).AddSingleton<IShiftCalendar>(shifts)
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

    private static HandlerOutcome.Conflict Flagged(string flag) => new(null, [flag]);

    private async Task<HandlerOutcome> StartAndEndAsync(DateTimeOffset startedAt, string startValue, DateTimeOffset endedAt, string endValue)
    {
        var root = Guid.CreateVersion7();
        await HandleAsync(TenantA, StartShiftEntry.Name, Start(root, startedAt, startValue));
        return await HandleAsync(TenantA, EndShiftEntry.Name, End(root, 2, endedAt, endValue)) switch
        {
            HandlerOutcome.Conflict conflict => conflict with { RecordId = null },
            var other => other,
        };
    }

    private static JsonObject Record(Guid root, DateTimeOffset startedAt, string startValue, Guid? meter = null) => new()
    {
        ["rootId"] = root.ToString(),
        ["version"] = 1,
        ["assetId"] = Asset.ToString(),
        ["meterId"] = (meter ?? HourMeter).ToString(),
        ["startedAt"] = startedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        ["startValue"] = startValue,
        ["fuelStartPercent"] = 75,
        ["startPhoto"] = Photo,
    };

    private JsonObject Start(Guid root, DateTimeOffset startedAt, string startValue, Guid? meter = null, Guid? previous = null, string? remarks = null, Guid? signer = null)
    {
        var record = Record(root, startedAt, startValue, meter);
        if (previous is { } id)
        {
            record["previousRootId"] = id.ToString();
        }

        if (remarks is not null)
        {
            record["remarks"] = remarks;
        }

        return Signed(record, 1, signer);
    }

    private JsonObject End(Guid root, int version, DateTimeOffset endedAt, string endValue, string? stoppages = null)
    {
        var record = new JsonObject
        {
            ["rootId"] = root.ToString(),
            ["version"] = version,
            ["endedAt"] = endedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["endValue"] = endValue,
            ["fuelEndPercent"] = 40,
            ["endPhoto"] = Photo,
        };
        if (stoppages is not null)
        {
            record["stoppages"] = JsonNode.Parse(stoppages);
        }

        return Signed(record, version);
    }

    /// <summary>The command payload: the record, an envelope naming it, and the tablet's signature over the envelope.</summary>
    private JsonObject Signed(JsonObject record, int version, Guid? signer = null)
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
        return new JsonObject { ["record"] = record, ["envelope"] = envelope, ["deviceSignature"] = devices.Sign(envelope) };
    }

    /// <summary>Runs a handler as the Sync module does: in the tenant, joined to the upload's transaction, rolled back when rejected.</summary>
    private async Task<HandlerOutcome> HandleAsync(Guid tenant, string type, JsonObject payload, Guid? device = null)
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
        var command = new VerifiedCommand(Guid.CreateVersion7(), device ?? Device, Site, 1, type, document.RootElement.Clone(), now, now, 0, payload.ToJsonString());

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
