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

/// <summary>
/// The checklist handler (ADR-0035 §3, §4) on real PostgreSQL: a signed checklist is stored once with its envelope,
/// checked against the template version it names; a critical Not OK opens a hold in the same transaction, and a
/// start on the held machine is stored but flagged <c>ASSET_UNSAFE</c>.
/// </summary>
[Trait("Story", "US-P2-FO-02")]
public sealed class ChecklistSubmissionHandlersTests(FieldOpsDatabase postgres) : IClassFixture<FieldOpsDatabase>, IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("01970000-0000-7000-8000-00000000000a");
    private static readonly Guid Site = Guid.Parse("01970000-0000-7000-8000-000000000c01");
    private static readonly Guid Asset = Guid.Parse("01970000-0000-7000-8000-000000000a01");
    private static readonly Guid Truck = Guid.Parse("01970000-0000-7000-8000-000000000a02");
    private static readonly Guid HourMeter = Guid.Parse("01970000-0000-7000-8000-000000000b01");
    private static readonly Guid Device = Guid.Parse("01970000-0000-7000-8000-000000000d01");
    private static readonly Guid Operator = Guid.Parse("01970000-0000-7000-8000-000000000501");
    private static readonly DateTimeOffset Morning = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
    private static readonly string Photo = "sha256:" + new string('b', 64);

    private readonly Assets assets = new();
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

        var meters = new Meters();
        meters.All[HourMeter] = new MeterSummary(HourMeter, Asset, "EX-01", "hours", "ok", [new AssetSiteAssignment(Site, Morning.AddDays(-30))]);
        services = new ServiceCollection().AddLogging()
            .AddTrustInfrastructure(database.App).AddFieldOpsInfrastructure(database.App)
            .AddSingleton<IDeviceDirectory>(devices).AddSingleton<IAssetDirectory>(assets).AddSingleton<IMeterDirectory>(meters).AddSingleton<IShiftCalendar>(new AnyTimeShift())
            .BuildServiceProvider(validateScopes: true);
        assets.All[Asset] = new AssetSummary(Asset, "EX-01", "excavator", [new AssetSiteAssignment(Site, Morning.AddDays(-30))]);
        assets.All[Truck] = new AssetSummary(Truck, "TR-01", "haul-truck", [new AssetSiteAssignment(Site, Morning.AddDays(-30))]);
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        devices.Dispose();
        NpgsqlConnection.ClearAllPools();
    }

    [Fact]
    public async Task An_all_ok_checklist_is_stored_once_with_its_envelope_and_opens_no_hold()
    {
        var template = await PublishAsync(required: true);
        var root = Guid.CreateVersion7();

        var first = await HandleAsync(Submit(root, template, Answers(("brakes", "ok", null), ("lights", "na", null))));
        var again = await HandleAsync(Submit(root, template, Answers(("brakes", "ok", null), ("lights", "na", null))));

        first.Should().Be(new HandlerOutcome.Accepted(root));
        again.Should().Be(SubmitChecklist.Exists);
        (await RowsAsync("SELECT asset_type || '|' || template_version || '|' || operator_id || '|' || site_id || '|' || cardinality(critical_items) FROM fieldops.checklist_submissions"))
            .Should().Equal($"excavator|1|{Operator}|{Site}|0");
        (await ScalarAsync<long>("SELECT count(*) FROM fieldops.checklist_submissions s JOIN audit.signature_envelopes e ON e.envelope_id = s.envelope_id")).Should().Be(1);
        (await ScalarAsync<long>("SELECT count(*) FROM fieldops.safety_holds")).Should().Be(0);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-004")]
    public async Task A_critical_not_ok_holds_the_machine_and_a_later_start_on_it_is_stored_flagged()
    {
        var template = await PublishAsync(required: true);
        var root = Guid.CreateVersion7();

        var outcome = await HandleAsync(Submit(root, template, Answers(("brakes", "not_ok", Photo), ("lights", "ok", null)), submittedAt: Morning));
        // Two operators, so the two open segments do not overlap one person's time.
        var before = await HandleAsync(Start(Morning.AddMinutes(-30), Guid.CreateVersion7()), StartShiftEntry.Name);
        var after = await HandleAsync(Start(Morning.AddMinutes(5), Guid.CreateVersion7()), StartShiftEntry.Name);

        outcome.Should().BeEquivalentTo(new HandlerOutcome.Conflict(root, [ShiftEntryFlags.AssetUnsafe]));
        (await RowsAsync("SELECT asset_id || '|' || site_id || '|' || array_to_string(items, ',') || '|' || opened_by FROM fieldops.safety_holds"))
            .Should().Equal($"{Asset}|{Site}|brakes|{Operator}");
        (await ScalarAsync<long>("SELECT count(*) FROM audit.ledger_entries WHERE event_type = 'fieldops.asset_marked_unsafe.v1'")).Should().Be(1);
        before.Should().BeOfType<HandlerOutcome.Accepted>("the machine was not held yet");
        after.Should().BeOfType<HandlerOutcome.Conflict>().Which.Flags.Should().Equal(ShiftEntryFlags.AssetUnsafe);
    }

    [Fact]
    public async Task A_start_after_the_hold_was_released_is_not_flagged()
    {
        var template = await PublishAsync(required: true);
        await HandleAsync(Submit(Guid.CreateVersion7(), template, Answers(("brakes", "not_ok", Photo), ("lights", "ok", null)), submittedAt: Morning));
        await ExecuteAsync("INSERT INTO fieldops.safety_hold_releases (id, tenant_id, asset_id, released_at, released_by, reason) "
            + $"SELECT id, tenant_id, asset_id, '{Morning.AddHours(1):O}', 'manager-1', 'Brakes adjusted and tested' FROM fieldops.safety_holds");

        var during = await HandleAsync(Start(Morning.AddMinutes(30), Guid.CreateVersion7()), StartShiftEntry.Name);
        var afterRelease = await HandleAsync(Start(Morning.AddHours(2), Guid.CreateVersion7()), StartShiftEntry.Name);

        during.Should().BeOfType<HandlerOutcome.Conflict>().Which.Flags.Should().Equal(ShiftEntryFlags.AssetUnsafe);
        afterRelease.Should().BeOfType<HandlerOutcome.Accepted>();
    }

    [Fact]
    public async Task A_pull_carries_the_newest_checklist_per_type_and_the_open_holds_until_released()
    {
        var v1 = await PublishAsync(required: true);
        await PublishAsync(required: false, extra: "horn");
        await HandleAsync(Submit(Guid.CreateVersion7(), v1, Answers(("brakes", "not_ok", Photo), ("lights", "ok", null)), submittedAt: Morning));

        var held = await PullAsync();
        await ExecuteAsync("INSERT INTO fieldops.safety_hold_releases (id, tenant_id, asset_id, released_at, released_by, reason) "
            + "SELECT id, tenant_id, asset_id, now(), 'manager-1', 'Brakes adjusted and tested' FROM fieldops.safety_holds");
        var released = await PullAsync();

        var templates = held.Single(d => d.Name == "checklistTemplates");
        templates.Replace.Should().BeTrue();
        templates.Upserts.Cast<ChecklistTemplateSyncRow>().Select(t => $"{t.AssetType} v{t.Version} {t.Required} {string.Join(",", t.Items.Select(i => i.Code))}")
            .Should().Equal("excavator v2 False brakes,lights,horn");
        var holds = held.Single(d => d.Name == "safetyHolds");
        holds.Replace.Should().BeTrue();
        holds.Upserts.Cast<SafetyHoldSyncRow>().Select(h => $"{h.AssetId} {string.Join(",", h.Items)} {h.OpenedAt:O}")
            .Should().Equal($"{Asset} brakes {Morning:O}");
        released.Single(d => d.Name == "safetyHolds").Upserts.Should().BeEmpty("a released hold leaves every tablet at the next pull");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-013")]
    public async Task A_submission_keeps_the_older_version_it_answered()
    {
        var v1 = await PublishAsync(required: true);
        await PublishAsync(required: false, extra: "horn");
        var root = Guid.CreateVersion7();

        (await HandleAsync(Submit(root, v1, Answers(("brakes", "ok", null), ("lights", "ok", null))))).Should().Be(new HandlerOutcome.Accepted(root));

        (await RowsAsync("SELECT template_version::text FROM fieldops.checklist_submissions")).Should().Equal("1");
    }

    [Fact]
    public async Task Answers_that_do_not_fit_the_version_or_the_machine_are_refused_and_nothing_is_written()
    {
        var template = await PublishAsync(required: true);
        JsonArray Ok() => Answers(("brakes", "ok", null), ("lights", "ok", null));

        (await HandleAsync(Submit(Guid.CreateVersion7(), template, Answers(("brakes", "ok", null))))).Should().Be(SubmitChecklist.Invalid, "every item is answered");
        (await HandleAsync(Submit(Guid.CreateVersion7(), template, Answers(("brakes", "not_ok", null), ("lights", "ok", null))))).Should().Be(SubmitChecklist.Invalid, "a critical Not OK needs its photo");
        (await HandleAsync(Submit(Guid.CreateVersion7(), template, Answers(("brakes", "ok", Photo), ("lights", "ok", null))))).Should().Be(SubmitChecklist.Invalid, "a photo goes with a Not OK");
        (await HandleAsync(Submit(Guid.CreateVersion7(), template, Answers(("brakes", "maybe", null), ("lights", "ok", null))))).Should().Be(SubmitChecklist.Invalid);
        (await HandleAsync(Submit(Guid.CreateVersion7(), template, Answers(("brakes", "ok", null), ("brakes", "ok", null))))).Should().Be(SubmitChecklist.Invalid);
        (await HandleAsync(Submit(Guid.CreateVersion7(), template, Ok(), templateVersion: 2))).Should().Be(SubmitChecklist.Invalid, "the server does not know that version");
        (await HandleAsync(Submit(Guid.CreateVersion7(), template, Ok(), asset: Truck))).Should().Be(SubmitChecklist.Invalid, "an excavator's checklist does not fit a truck");
        (await HandleAsync(Submit(Guid.CreateVersion7(), template, Ok(), asset: Guid.CreateVersion7()))).Should().Be(SubmitChecklist.AssetUnknown);
        (await HandleAsync(Submit(Guid.CreateVersion7(), template, Ok(), recordType: ShiftEntryRules.RecordType))).Should().Be(SignedRecord.EnvelopeMismatch);

        (await ScalarAsync<long>("SELECT (SELECT count(*) FROM fieldops.checklist_submissions) + (SELECT count(*) FROM fieldops.safety_holds) + (SELECT count(*) FROM audit.signature_envelopes)"))
            .Should().Be(0);
    }

    /// <summary>Publishes the next version of the excavator checklist: brakes (critical) and lights, plus an extra item.</summary>
    private async Task<ChecklistTemplate> PublishAsync(bool required, string? extra = null)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(TenantA));
        var store = scope.ServiceProvider.GetRequiredService<IChecklistTemplateStore>();
        var texts = new Dictionary<string, string> { ["en"] = "x" };
        List<ChecklistItem> items = [new("brakes", texts, true, true), new("lights", texts, false, false)];
        if (extra is not null)
        {
            items.Add(new(extra, texts, false, false));
        }

        var versions = await store.VersionsAsync("excavator", Ct);
        var template = ChecklistTemplate.Publish(TimeProvider.System, new TenantId(TenantA), "excavator", versions.Count + 1, required, items, "admin-1");
        (await store.TryAddAsync(template, Ct)).Should().BeTrue();
        return template;
    }

    private static JsonArray Answers(params (string Item, string Answer, string? Photo)[] answers) =>
        [.. answers.Select(a => (JsonNode)(a.Photo is null
            ? new JsonObject { ["item"] = a.Item, ["answer"] = a.Answer }
            : new JsonObject { ["item"] = a.Item, ["answer"] = a.Answer, ["photo"] = a.Photo }))];

    private JsonObject Submit(Guid root, ChecklistTemplate template, JsonArray answers, DateTimeOffset? submittedAt = null, int? templateVersion = null, Guid? asset = null, string? recordType = null) =>
        Signed(new JsonObject
        {
            ["rootId"] = root.ToString(),
            ["version"] = 1,
            ["templateId"] = template.Id.Value.ToString(),
            ["templateVersion"] = templateVersion ?? template.Version,
            ["assetId"] = (asset ?? Asset).ToString(),
            ["submittedAt"] = (submittedAt ?? Morning).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
            ["answers"] = answers,
        }, recordType ?? ChecklistRules.RecordType);

    private JsonObject Start(DateTimeOffset startedAt, Guid signer) => Signed(new JsonObject
    {
        ["rootId"] = Guid.CreateVersion7().ToString(),
        ["version"] = 1,
        ["assetId"] = Asset.ToString(),
        ["meterId"] = HourMeter.ToString(),
        ["startedAt"] = startedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
        ["startValue"] = "100.0",
        ["fuelStartPercent"] = 80,
        ["startPhoto"] = Photo,
    }, ShiftEntryRules.RecordType, signer);

    private JsonObject Signed(JsonObject record, string recordType, Guid? signer = null)
    {
        using var recordDocument = JsonDocument.Parse(record.ToJsonString());
        var envelope = new JsonObject
        {
            ["v"] = 1,
            ["envelopeId"] = Guid.CreateVersion7().ToString(),
            ["recordType"] = recordType,
            ["recordId"] = record["rootId"]!.GetValue<string>(),
            ["recordVersion"] = 1,
            ["payloadHash"] = SignatureEnvelopes.PayloadHash(recordDocument.RootElement),
            ["signer"] = new JsonObject { ["userId"] = (signer ?? Operator).ToString(), ["role"] = "operator", ["deviceId"] = Device.ToString() },
            ["context"] = new JsonObject { ["deviceTime"] = "2026-11-09T06:02:11.250+00:00", ["appVersion"] = "1.5.0" },
        };
        return new JsonObject { ["record"] = record, ["envelope"] = envelope, ["deviceSignature"] = devices.Sign(envelope) };
    }

    /// <summary>Runs a handler as the Sync module does: in the tenant, joined to the upload's transaction, rolled back when rejected.</summary>
    private async Task<HandlerOutcome> HandleAsync(JsonObject payload, string type = SubmitChecklist.Name)
    {
        using var document = JsonDocument.Parse(payload.ToJsonString());
        await using var connection = new NpgsqlConnection(database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(TenantA));
        var handler = (ISyncCommandHandler)scope.ServiceProvider.GetRequiredService(scope.ServiceProvider.GetRequiredService<SyncCommandTypes>().HandlerOf(type)!);
        var now = DateTimeOffset.UtcNow;
        var command = new VerifiedCommand(Guid.CreateVersion7(), Device, Site, 1, type, document.RootElement.Clone(), now, now, 0, payload.ToJsonString());

        var outcome = await handler.HandleAsync(command, new Joined(connection, transaction), Ct);
        await (outcome is HandlerOutcome.Rejected ? transaction.RollbackAsync(Ct) : transaction.CommitAsync(Ct));
        return outcome;
    }

    private async Task<List<string>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>The pull's collections from this module, as the Sync module asks for them (in the tenant, a snapshot).</summary>
    private async Task<List<SyncDelta>> PullAsync()
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(TenantA));
        var deltas = new List<SyncDelta>();
        foreach (var source in scope.ServiceProvider.GetServices<ISyncSource>())
        {
            deltas.AddRange(await source.ChangesAsync(new SyncScope(Device, Site), null, Ct));
        }

        return deltas;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed record Joined(DbConnection Connection, DbTransaction Transaction) : ISyncTransaction;

    private sealed class Assets : IAssetDirectory
    {
        public Dictionary<Guid, AssetSummary> All { get; } = [];

        public Task<AssetSummary?> FindAsync(Guid assetId, CancellationToken cancellationToken) => Task.FromResult(All.GetValueOrDefault(assetId));
    }

    private sealed class Meters : IMeterDirectory
    {
        public Dictionary<Guid, MeterSummary> All { get; } = [];

        public Task<MeterSummary?> FindAsync(Guid meterId, CancellationToken cancellationToken) => Task.FromResult(All.GetValueOrDefault(meterId));
    }

    /// <summary>Every start falls in a shift here: holds are what these tests look at.</summary>
    private sealed class AnyTimeShift : IShiftCalendar
    {
        public Task<ResolvedShift?> ResolveAsync(Guid siteId, DateTimeOffset instant, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedShift?>(new ResolvedShift(DateOnly.FromDateTime(instant.UtcDateTime), "D", "Day", instant.AddHours(-1), instant.AddHours(7), "Etc/UTC"));
    }

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
