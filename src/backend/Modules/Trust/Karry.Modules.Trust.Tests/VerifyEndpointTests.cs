using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Trust.Api;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Karry.Modules.Trust.Tests;

/// <summary><c>POST /api/v1/audit/verify</c> verifies the caller's tenant chain only (US-P1-AUD-03).</summary>
[Trait("Story", "US-P1-AUD-03")]
public sealed class VerifyEndpointTests(LedgerDatabase postgres) : IClassFixture<LedgerDatabase>
{
    [Fact]
    public async Task The_endpoint_verifies_the_tokens_tenant_chain_and_reports_a_break()
    {
        var database = await postgres.Server.NewKarryDatabaseAsync();
        var tenant = Guid.Parse("01970000-0000-7000-8000-0000000000aa");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Karry"] = database.App });
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryTenancy(builder.Configuration);
        builder.Services.TrustTestIssuer();
        builder.Services.AddModules(builder.Configuration, [new TrustModule()]);
        await using var app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
        app.MapModules();
        await MigrationRunner.MigrateAsync(app.Services, database.Owner, TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        await SeedChainAsync(database.Admin, tenant);

        var intact = await VerifyAsync(client, tenant);
        await ExecuteAsync(database.Admin, "SET session_replication_role = replica; UPDATE audit.ledger_entries SET event_type = 'test.forged.v1' WHERE seq = 2;");
        var broken = await VerifyAsync(client, tenant);
        var otherTenant = await VerifyAsync(client, Guid.CreateVersion7());

        intact.GetProperty("valid").GetBoolean().Should().BeTrue();
        intact.GetProperty("entriesChecked").GetInt64().Should().Be(2);
        broken.GetProperty("valid").GetBoolean().Should().BeFalse();
        broken.GetProperty("firstBrokenSequence").GetInt64().Should().Be(2);
        otherTenant.GetProperty("entriesChecked").GetInt64().Should().Be(0, "another tenant sees an empty chain, never this one");
    }

    private static async Task<JsonElement> VerifyAsync(HttpClient client, Guid tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/audit/verify");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(tenant));
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    /// <summary>Two valid entries written directly with the hash functions (their spec is pinned by LedgerHashTests).</summary>
    private static async Task SeedChainAsync(string admin, Guid tenant)
    {
        var previous = Domain.LedgerHash.Genesis;
        var at = Domain.LedgerHash.ToStoragePrecision(DateTimeOffset.UtcNow);
        for (var seq = 1; seq <= 2; seq++)
        {
            var payload = $$"""{"n":{{seq}}}""";
            var payloadHash = Domain.LedgerHash.OfPayload(payload);
            var hash = Domain.LedgerHash.OfEntry(previous, new(tenant), seq, "test.happened.v1", payloadHash, at);
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO audit.ledger_entries (tenant_id, seq, event_id, event_type, occurred_at, payload, payload_hash, prev_hash, entry_hash) " +
                "VALUES (@t, @s, @e, 'test.happened.v1', @at, @p, @ph, @prev, @h)", connection);
            insert.Parameters.AddWithValue("t", tenant);
            insert.Parameters.AddWithValue("s", (long)seq);
            insert.Parameters.AddWithValue("e", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("at", at);
            insert.Parameters.AddWithValue("p", payload);
            insert.Parameters.AddWithValue("ph", payloadHash);
            insert.Parameters.AddWithValue("prev", previous);
            insert.Parameters.AddWithValue("h", hash);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            previous = hash;
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
