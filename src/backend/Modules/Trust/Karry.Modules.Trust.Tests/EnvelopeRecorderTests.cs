using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Trust.Contracts;
using Karry.Modules.Trust.Infrastructure;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Trust.Tests;

/// <summary>
/// Envelopes stored with a tablet's command (ADR-0033 §4) on real PostgreSQL with the real roles and migration:
/// checked, append-only, with their ledger entry in the same transaction.
/// </summary>
[Trait("Story", "US-P2-SIG-02")]
public sealed class EnvelopeRecorderTests(LedgerDatabase postgres) : IClassFixture<LedgerDatabase>, IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("01970000-0000-7000-8000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("01970000-0000-7000-8000-00000000000b");
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 9, 6, 3, 0, TimeSpan.Zero);
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "tests", "vectors", "signature-envelope.json"))).RootElement;

    private readonly Devices devices = new();
    private KarryDatabase database = null!;
    private ServiceProvider services = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Minimal => Vectors.GetProperty("valid")[0];

    private static Guid VectorDevice => Guid.Parse(Minimal.GetProperty("envelope").GetProperty("signer").GetProperty("deviceId").GetString()!);

    public async ValueTask InitializeAsync()
    {
        database = await postgres.Server.NewKarryDatabaseAsync();
        await using (var migrations = new ServiceCollection().AddLogging().AddTrustInfrastructure(database.App).BuildServiceProvider())
        {
            await MigrationRunner.MigrateAsync(migrations, database.Owner, CancellationToken.None);
        }

        services = new ServiceCollection().AddLogging().AddTrustInfrastructure(database.App).AddSingleton<IDeviceDirectory>(devices).BuildServiceProvider();
        var key = Vectors.GetProperty("devicePublicKey");
        devices.Keys[VectorDevice] = (Base64Url.DecodeFromChars(key.GetProperty("x").GetString()), Base64Url.DecodeFromChars(key.GetProperty("y").GetString()));
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }

    [Fact]
    public async Task A_valid_envelope_is_stored_as_signed_with_its_ledger_entry_and_a_second_copy_adds_nothing()
    {
        var submission = Submission(TenantA, VectorDevice, Minimal.GetProperty("envelope"), Minimal.GetProperty("deviceSignature").GetString()!);

        var first = await RecordAsync(submission);
        var again = await RecordAsync(submission with { CommandId = Guid.CreateVersion7() });

        var hash = Minimal.GetProperty("envelopeHash").GetString()!;
        first.Should().Be(new EnvelopeOutcome.Recorded(hash));
        again.Should().Be(new EnvelopeOutcome.AlreadyRecorded(hash));
        (await ScalarAsync<string>(database.Admin, "SELECT canonical FROM audit.signature_envelopes")).Should().Be(Minimal.GetProperty("canonical").GetString());
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.signature_envelopes")).Should().Be(1);
        var ledger = await ScalarAsync<string>(database.Admin, $"SELECT payload::text FROM audit.ledger_entries WHERE event_type = '{EnvelopeLedger.ReceivedEvent}'");
        JsonNode.Parse(ledger)!["envelopeHash"]!.GetValue<string>().Should().Be(hash);
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.ledger_entries")).Should().Be(1);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-002")]
    public async Task An_envelope_changed_after_signing_is_refused_and_nothing_is_written()
    {
        using var changed = JsonDocument.Parse(Minimal.GetProperty("tamperedCanonical").GetString()!);

        var outcome = await RecordAsync(Submission(TenantA, VectorDevice, changed.RootElement, Minimal.GetProperty("deviceSignature").GetString()!));

        outcome.Should().Be(new EnvelopeOutcome.Refused(EnvelopeCodes.SignatureInvalid));
        (await ScalarAsync<long>(database.Admin, "SELECT (SELECT count(*) FROM audit.signature_envelopes) + (SELECT count(*) FROM audit.ledger_entries)")).Should().Be(0);
    }

    [Fact]
    public async Task An_envelope_naming_another_tablet_a_malformed_one_and_an_unknown_tablet_are_refused()
    {
        var envelope = Minimal.GetProperty("envelope");
        var signature = Minimal.GetProperty("deviceSignature").GetString()!;
        using var malformed = JsonDocument.Parse(Vectors.GetProperty("invalid")[0].GetProperty("envelope").GetRawText());
        var unknownDevice = Guid.CreateVersion7();
        using var otherTablet = Signed(Guid.CreateVersion7(), unknownDevice, 1, out var unknownSignature);

        (await RecordAsync(Submission(TenantA, Guid.CreateVersion7(), envelope, signature))).Should().Be(new EnvelopeOutcome.Refused(EnvelopeCodes.DeviceMismatch));
        (await RecordAsync(Submission(TenantA, VectorDevice, malformed.RootElement, signature))).Should().Be(new EnvelopeOutcome.Refused(EnvelopeCodes.Invalid));
        devices.Keys.Remove(unknownDevice);
        (await RecordAsync(Submission(TenantA, unknownDevice, otherTablet.RootElement, unknownSignature))).Should().Be(new EnvelopeOutcome.Refused(EnvelopeCodes.SignatureInvalid));
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-003")]
    public async Task A_revoked_tablets_envelope_is_refused_even_with_a_valid_signature_and_nothing_is_written()
    {
        devices.Revoked.Add(VectorDevice);

        var outcome = await RecordAsync(Submission(TenantA, VectorDevice, Minimal.GetProperty("envelope"), Minimal.GetProperty("deviceSignature").GetString()!));

        outcome.Should().Be(new EnvelopeOutcome.Refused(EnvelopeCodes.DeviceRevoked));
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.signature_envelopes")).Should().Be(0);
        (await ScalarAsync<long>(database.Admin, "SELECT count(*) FROM audit.ledger_entries")).Should().Be(0);
    }

    [Fact]
    public async Task The_same_envelope_ID_with_other_content_is_refused()
    {
        var (id, device) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        using var first = Signed(id, device, 1, out var firstSignature);
        using var second = Signed(id, device, 2, out var secondSignature);

        (await RecordAsync(Submission(TenantA, device, first.RootElement, firstSignature))).Should().BeOfType<EnvelopeOutcome.Recorded>();
        (await RecordAsync(Submission(TenantA, device, second.RootElement, secondSignature))).Should().Be(new EnvelopeOutcome.Refused(EnvelopeCodes.Conflict));
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-007")]
    public async Task The_application_role_can_neither_change_nor_delete_an_envelope_and_another_company_does_not_see_it()
    {
        await RecordAsync(Submission(TenantA, VectorDevice, Minimal.GetProperty("envelope"), Minimal.GetProperty("deviceSignature").GetString()!));

        foreach (var sql in new[] { "UPDATE audit.signature_envelopes SET record_version = 2", "DELETE FROM audit.signature_envelopes", "TRUNCATE audit.signature_envelopes" })
        {
            await FluentActions.Awaiting(() => AsTenantAsync(TenantA, sql)).Should().ThrowAsync<PostgresException>(sql);
        }

        // The owner sees the row only under its tenant (RLS is forced), and then the trigger refuses the change.
        await FluentActions.Awaiting(() => ExecuteAsync(database.Owner, $"SELECT set_config('app.tenant_id', '{TenantA}', false); UPDATE audit.signature_envelopes SET record_version = 2"))
            .Should().ThrowAsync<PostgresException>("the trigger refuses even the owner").Where(e => e.MessageText.Contains("append-only", StringComparison.Ordinal));
        (await AsTenantScalarAsync(TenantB, "SELECT count(*) FROM audit.signature_envelopes")).Should().Be(0);
        (await AsTenantScalarAsync(TenantA, "SELECT count(*) FROM audit.signature_envelopes")).Should().Be(1);
    }

    private static EnvelopeSubmission Submission(Guid tenant, Guid device, JsonElement envelope, string signature) =>
        new(tenant, device, Guid.CreateVersion7(), envelope, signature, ReceivedAt);

    /// <summary>An envelope signed here by <paramref name="device"/>'s key (made on first use; <see cref="devices"/> then knows it).</summary>
    private JsonDocument Signed(Guid envelopeId, Guid device, int recordVersion, out string signature)
    {
        var node = JsonNode.Parse(Minimal.GetProperty("envelope").GetRawText())!;
        node["envelopeId"] = envelopeId.ToString();
        node["recordVersion"] = recordVersion;
        node["signer"]!["deviceId"] = device.ToString();
        var document = JsonDocument.Parse(node.ToJsonString());
        if (!devices.Private.TryGetValue(device, out var parameters))
        {
            using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            parameters = fresh.ExportParameters(true);
            devices.Private[device] = parameters;
            devices.Keys[device] = (parameters.Q.X!, parameters.Q.Y!);
        }

        using var key = ECDsa.Create(parameters);
        signature = Base64Url.EncodeToString(key.SignData(SignatureEnvelopes.Canonical(document.RootElement), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        return document;
    }

    private async Task<EnvelopeOutcome> RecordAsync(EnvelopeSubmission submission)
    {
        await using var connection = new NpgsqlConnection(database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var scope = services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IEnvelopeRecorder>().RecordAsync(connection, transaction, submission, Ct);
        await transaction.CommitAsync(Ct);
        return outcome;
    }

    private async Task AsTenantAsync(Guid tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenant}', true); {sql}", connection, transaction);
        await command.ExecuteNonQueryAsync(Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task<long> AsTenantScalarAsync(Guid tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
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

    /// <summary>The enrolled tablets' public keys (Identity's directory in the host).</summary>
    private sealed class Devices : IDeviceDirectory
    {
        public Dictionary<Guid, (byte[] X, byte[] Y)> Keys { get; } = [];

        public Dictionary<Guid, ECParameters> Private { get; } = [];

        public HashSet<Guid> Revoked { get; } = [];

        public Task<DeviceKey?> FindAsync(Guid deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(Keys.TryGetValue(deviceId, out var key) ? new DeviceKey(deviceId, Guid.Empty, key.X, key.Y, Revoked.Contains(deviceId)) : null);

        public Task<bool> RememberAssertionAsync(Guid deviceId, string assertionId, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
