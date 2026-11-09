using System.Buffers.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Contracts;
using Karry.Modules.Trust.Infrastructure;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Trust.Tests;

/// <summary>PostgreSQL and OpenBao for the countersigning tests.</summary>
public sealed class CountersignServices : IAsyncLifetime
{
    public PostgresServer Postgres { get; private set; } = null!;

    public OpenBaoServer Bao { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var postgres = PostgresServer.StartAsync();
        var bao = OpenBaoServer.StartAsync();
        (Postgres, Bao) = (await postgres, await bao);
    }

    public async ValueTask DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Bao.DisposeAsync();
    }
}

/// <summary>
/// The countersign job end to end (ADR-0033 §4, US-P2-SIG-03): envelopes stored with a tablet's command are signed by
/// the server key in OpenBao, the key version is published, and anyone can verify from the database alone.
/// </summary>
[Trait("Story", "US-P2-SIG-03")]
public sealed class CountersignTests(CountersignServices stack) : IClassFixture<CountersignServices>, IAsyncLifetime
{
    private static readonly TenantId Tenant = new(Guid.Parse("01970000-0000-7000-8000-00000000000a"));
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "tests", "vectors", "signature-envelope.json"))).RootElement;

    private readonly string keyName = $"k-{Guid.NewGuid():N}";
    private KarryDatabase database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        database = await stack.Postgres.NewKarryDatabaseAsync();
        await using var migrations = new ServiceCollection().AddLogging().AddTrustInfrastructure(database.App).BuildServiceProvider();
        await MigrationRunner.MigrateAsync(migrations, database.Owner, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-001")]
    public async Task A_stored_envelope_is_countersigned_once_and_verifies_with_the_published_key()
    {
        await using var services = Services(stack.Bao.Address);
        var hash = await RecordAsync(services, Vectors.GetProperty("valid")[0]);

        var first = await services.GetRequiredService<CountersignEnvelopes>().RunAsync(Ct);
        var second = await services.GetRequiredService<CountersignEnvelopes>().RunAsync(Ct);

        (first.Countersigned, first.OldestPending, second.Countersigned).Should().Be((1, null, 0));
        (await CountersignatureVerifiesAsync(hash)).Should().Be($"{keyName}-v1");
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-006")]
    public async Task While_the_signing_service_is_down_envelopes_stay_pending_and_are_countersigned_after_it_recovers()
    {
        // Nothing listens on port 1: the service is down.
        await using var down = Services("http://127.0.0.1:1");
        var hash = await RecordAsync(down, Vectors.GetProperty("valid")[0]);

        var outage = await down.GetRequiredService<CountersignEnvelopes>().RunAsync(Ct);

        (outage.SignerDown, outage.Countersigned, outage.Failed).Should().Be((true, 0, 1));
        outage.OldestPending.Should().NotBeNull("the lag keeps being measured, so the alert can fire");
        await using var recovered = Services(stack.Bao.Address);
        (await recovered.GetRequiredService<CountersignEnvelopes>().RunAsync(Ct)).Countersigned.Should().Be(1);
        (await CountersignatureVerifiesAsync(hash)).Should().NotBeNull();
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-004")]
    public async Task After_a_key_rotation_the_new_version_is_published_and_both_countersignatures_verify()
    {
        await using var services = Services(stack.Bao.Address);
        var before = await RecordAsync(services, Vectors.GetProperty("valid")[0]);
        await services.GetRequiredService<CountersignEnvelopes>().RunAsync(Ct);

        await stack.Bao.ExecAsync("bao", "write", "-f", $"transit/keys/{keyName}/rotate");
        var after = await RecordAsync(services, Vectors.GetProperty("valid")[1]);
        await services.GetRequiredService<CountersignEnvelopes>().RunAsync(Ct);

        (await CountersignatureVerifiesAsync(before)).Should().Be($"{keyName}-v1");
        (await CountersignatureVerifiesAsync(after)).Should().Be($"{keyName}-v2");
        (await ScalarAsync<long>("SELECT count(*) FROM audit.server_keys")).Should().Be(2);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-007")]
    public async Task Countersignatures_and_published_keys_cannot_be_changed()
    {
        await using var services = Services(stack.Bao.Address);
        await RecordAsync(services, Vectors.GetProperty("valid")[0]);
        await services.GetRequiredService<CountersignEnvelopes>().RunAsync(Ct);

        await FluentActions.Awaiting(() => ExecuteAsync(database.App, $"SELECT set_config('app.tenant_id', '{Tenant}', false); UPDATE audit.envelope_countersignatures SET signature = 'x'"))
            .Should().ThrowAsync<PostgresException>();
        await FluentActions.Awaiting(() => ExecuteAsync(database.App, "UPDATE audit.server_keys SET public_key = 'x'")).Should().ThrowAsync<PostgresException>();
        await FluentActions.Awaiting(() => ExecuteAsync(database.Owner, "DELETE FROM audit.server_keys"))
            .Should().ThrowAsync<PostgresException>().Where(e => e.MessageText.Contains("append-only", StringComparison.Ordinal));
    }

    private ServiceProvider Services(string baoAddress)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Trust:ServerSigning:Address"] = baoAddress,
            ["Trust:ServerSigning:Token"] = OpenBaoServer.RootToken,
            ["Trust:ServerSigning:KeyName"] = keyName,
            ["Trust:ServerSigning:CreateKeyIfMissing"] = "true",
            ["Trust:ServerSigning:Timeout"] = "00:00:03",
        };
        var key = Vectors.GetProperty("devicePublicKey");
        var device = Guid.Parse(Vectors.GetProperty("valid")[0].GetProperty("envelope").GetProperty("signer").GetProperty("deviceId").GetString()!);
        return new ServiceCollection()
            .AddLogging()
            .AddTrustInfrastructure(database.App)
            .AddTrustServerSigning(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddSingleton<ITenantDirectory>(new OneTenant(Tenant))
            .AddSingleton<IDeviceDirectory>(new OneDevice(new DeviceKey(device, Guid.Empty,
                Base64Url.DecodeFromChars(key.GetProperty("x").GetString()), Base64Url.DecodeFromChars(key.GetProperty("y").GetString()), false)))
            .BuildServiceProvider();
    }

    /// <summary>Stores a vector envelope as a tablet's command handler would, and returns its hash.</summary>
    private async Task<string> RecordAsync(ServiceProvider services, JsonElement vector)
    {
        await using var connection = new NpgsqlConnection(database.App);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var scope = services.CreateAsyncScope();
        var submission = new EnvelopeSubmission(Tenant.Value, Guid.Parse(vector.GetProperty("envelope").GetProperty("signer").GetProperty("deviceId").GetString()!),
            Guid.CreateVersion7(), vector.GetProperty("envelope"), vector.GetProperty("deviceSignature").GetString()!, DateTimeOffset.UtcNow);
        var outcome = await scope.ServiceProvider.GetRequiredService<IEnvelopeRecorder>().RecordAsync(connection, transaction, submission, Ct);
        await transaction.CommitAsync(Ct);
        return outcome.Should().BeOfType<EnvelopeOutcome.Recorded>().Subject.EnvelopeHash;
    }

    /// <summary>
    /// Checks the countersignature of an envelope as an outside verifier would, from the database alone: the published
    /// key of its kid, over the countersigned text rebuilt from the stored hash and receipt time. Returns the kid.
    /// </summary>
    private async Task<string?> CountersignatureVerifiesAsync(string envelopeHash)
    {
        await using var connection = new NpgsqlConnection(database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT e.received_at, c.key_id, c.signature, k.public_key
            FROM audit.signature_envelopes e
            JOIN audit.envelope_countersignatures c ON c.envelope_id = e.envelope_id
            JOIN audit.server_keys k ON k.key_id = c.key_id
            WHERE e.envelope_hash = @hash
            """, connection);
        command.Parameters.AddWithValue("hash", envelopeHash);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct))
        {
            return null;
        }

        var digest = Countersignatures.Digest(envelopeHash, reader.GetFieldValue<DateTimeOffset>(0));
        ServerSignatures.Verify(reader.GetString(3), digest, Base64Url.DecodeFromChars(reader.GetString(2))).Should().BeTrue();
        return reader.GetString(1);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(database.Admin);
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

    private sealed class OneTenant(TenantId tenant) : ITenantDirectory
    {
        public Task<IReadOnlyList<TenantId>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TenantId>>([tenant]);

        public Task<TenantSummary?> FindAsync(TenantId tenant, CancellationToken cancellationToken) => Task.FromResult<TenantSummary?>(null);
    }

    private sealed class OneDevice(DeviceKey key) : IDeviceDirectory
    {
        public Task<DeviceKey?> FindAsync(Guid deviceId, CancellationToken cancellationToken) => Task.FromResult(deviceId == key.DeviceId ? key : null);

        public Task<bool> RememberAssertionAsync(Guid deviceId, string assertionId, DateTimeOffset expiresAt, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
