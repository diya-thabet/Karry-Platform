using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Devices;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Tenancy.Contracts;
using Karry.Modules.Trust.Api;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Trust.Tests;

/// <summary>
/// <c>GET /api/v1/audit/envelopes/{id}/verification</c> (US-P2-SIG-04, ADR-0033 §6): every check of a stored envelope
/// from the database alone, and the one that breaks when something was changed behind the application's back.
/// </summary>
[Trait("Story", "US-P2-SIG-04")]
public sealed class EnvelopeVerificationTests(CountersignServices stack) : IClassFixture<CountersignServices>
{
    private static readonly Guid Tenant = Guid.Parse("01970000-0000-7000-8000-0000000000aa");
    private static readonly JsonElement Vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "tests", "vectors", "signature-envelope.json"))).RootElement;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P2-SIG-001")]
    public async Task An_envelope_is_pending_until_countersigned_then_verified_and_another_company_cannot_see_it()
    {
        await using var host = await Host.StartAsync(stack);
        var id = await host.RecordAsync(Vectors.GetProperty("valid")[0]);

        var pending = await host.VerifyAsync(id);
        await host.App.Services.GetRequiredService<CountersignEnvelopes>().RunAsync(Ct);
        var verified = await host.VerifyAsync(id);
        var elsewhere = await host.GetAsync(id, Guid.CreateVersion7());

        pending.Status.Should().Be("pending");
        pending.Checks.Should().Equal(new Dictionary<string, string>
        {
            ["envelope"] = "pass", ["device_signature"] = "pass", ["server_countersignature"] = "pending", ["ledger_entry"] = "pass", ["record_payload"] = "not_checked",
        });
        verified.Status.Should().Be("verified");
        verified.Checks["server_countersignature"].Should().Be("pass");
        elsewhere.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [Trait("TestCase", "TC-P2-SIG-002")]
    [InlineData("envelope text", "UPDATE audit.signature_envelopes SET canonical = replace(canonical, '\"recordVersion\":1', '\"recordVersion\":2')", "envelope")]
    [InlineData("countersignature", "UPDATE audit.envelope_countersignatures SET signature = reverse(signature)", "server_countersignature")]
    [InlineData("ledger entry", "UPDATE audit.ledger_entries SET occurred_at = occurred_at + interval '1 hour'", "ledger_entry")]
    [InlineData("published key", "DELETE FROM audit.server_keys", "server_countersignature")]
    [InlineData("device signature", "UPDATE audit.signature_envelopes SET device_signature = reverse(device_signature)", "device_signature")]
    public async Task A_change_made_behind_the_applications_back_is_named_by_the_check_it_breaks(string what, string forge, string broken)
    {
        await using var host = await Host.StartAsync(stack);
        var id = await host.RecordAsync(Vectors.GetProperty("valid")[0]);
        await host.App.Services.GetRequiredService<CountersignEnvelopes>().RunAsync(Ct);

        // As the database superuser, with triggers off: what an insider with full access could do.
        await ExecuteAsync(host.Database.Admin, $"SET session_replication_role = replica; {forge}");
        var result = await host.VerifyAsync(id);

        result.Status.Should().Be("invalid", what);
        result.Checks[broken].Should().Be("fail", what);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed record Verification(string Status, Dictionary<string, string> Checks);

    /// <summary>A host with the Trust module on a fresh database, signing with OpenBao, and one enrolled tablet.</summary>
    private sealed class Host(WebApplication app, KarryDatabase database, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App => app;

        public KarryDatabase Database => database;

        public static async Task<Host> StartAsync(CountersignServices stack)
        {
            var database = await stack.Postgres.NewKarryDatabaseAsync();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Karry"] = database.App,
                ["Trust:ServerSigning:Address"] = stack.Bao.Address,
                ["Trust:ServerSigning:Token"] = OpenBaoServer.RootToken,
                ["Trust:ServerSigning:KeyName"] = $"k-{Guid.NewGuid():N}",
                ["Trust:ServerSigning:CreateKeyIfMissing"] = "true",
            });
            builder.Services.AddKarryApiConventions();
            builder.Services.AddKarryTenancy(builder.Configuration);
            builder.Services.TrustTestIssuer();
            builder.Services.AddModules(builder.Configuration, [new TrustModule()]);
            builder.Services.TreatEveryoneAs(DefaultRoles.Controller);
            var key = Vectors.GetProperty("devicePublicKey");
            var device = Guid.Parse(Vectors.GetProperty("valid")[0].GetProperty("envelope").GetProperty("signer").GetProperty("deviceId").GetString()!);
            builder.Services.AddSingleton<IDeviceDirectory>(new OneDevice(new DeviceKey(device, Guid.Empty,
                Base64Url.DecodeFromChars(key.GetProperty("x").GetString()), Base64Url.DecodeFromChars(key.GetProperty("y").GetString()), false)));
            builder.Services.AddSingleton<ITenantDirectory>(new OneTenant(new TenantId(Tenant)));
            var app = builder.Build();
            app.UseKarryApiConventions();
            app.UseKarryTenancy();
            app.MapModules();
            await MigrationRunner.MigrateAsync(app.Services, database.Owner, Ct);
            await app.StartAsync(Ct);
            return new Host(app, database, app.GetTestClient());
        }

        public async Task<Guid> RecordAsync(JsonElement vector)
        {
            await using var connection = new NpgsqlConnection(database.App);
            await connection.OpenAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using var scope = app.Services.CreateAsyncScope();
            var envelope = vector.GetProperty("envelope");
            var submission = new EnvelopeSubmission(Tenant, Guid.Parse(envelope.GetProperty("signer").GetProperty("deviceId").GetString()!), Guid.CreateVersion7(),
                envelope, vector.GetProperty("deviceSignature").GetString()!, DateTimeOffset.UtcNow);
            (await scope.ServiceProvider.GetRequiredService<IEnvelopeRecorder>().RecordAsync(connection, transaction, submission, Ct))
                .Should().BeOfType<EnvelopeOutcome.Recorded>();
            await transaction.CommitAsync(Ct);
            return Guid.Parse(envelope.GetProperty("envelopeId").GetString()!);
        }

        public async Task<HttpResponseMessage> GetAsync(Guid envelopeId, Guid tenant)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/audit/envelopes/{envelopeId}/verification");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant));
            return await client.SendAsync(request, Ct);
        }

        public async Task<Verification> VerifyAsync(Guid envelopeId)
        {
            using var response = await GetAsync(envelopeId, Tenant);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            return new Verification(
                body.GetProperty("status").GetString()!,
                body.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("result").GetString()!));
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
        }
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
