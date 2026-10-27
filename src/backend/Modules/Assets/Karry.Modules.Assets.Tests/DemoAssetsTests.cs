using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Modules.Assets.Application;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Assets.Tests;

/// <summary>The demo seed (P1.13): machines on their sites with meters and a reading through the real operations, ledgered, idempotent.</summary>
[Trait("Story", "US-P1-AST-03")]
public sealed class DemoAssetsTests(AssetsHost host) : IClassFixture<AssetsHost>
{
    private static readonly DemoAsset[] Demo =
    [
        new("CAR-NORD", Machine("PEL-01", "excavator", [new MeterInput("hours", 9850.0m, new DateOnly(2019, 3, 1))]), new Dictionary<string, decimal> { ["hours"] = 9.5m }),
        new("CAR-SUD", Machine("TOMB-01", "haul-truck", [new MeterInput("hours", 12310.5m, new DateOnly(2018, 3, 1)), new MeterInput("km", 186420.0m, new DateOnly(2018, 3, 1))]),
            new Dictionary<string, decimal> { ["hours"] = 10.5m, ["km"] = 86.4m }),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeds_machines_on_their_sites_with_a_reading_after_the_initial_one_in_the_ledger_once()
    {
        var tenant = await ProvisionAsync();
        await SiteAsync(tenant, "CAR-NORD");
        await SiteAsync(tenant, "CAR-SUD");

        (await SeedAsync(tenant, Demo)).Should().Be(2);
        (await SeedAsync(tenant, Demo)).Should().Be(0, "a company that has machines is left as it is");

        (await ScalarAsync<string>($"""
            SELECT string_agg(a.code || '@' || s.code, ',' ORDER BY a.code)
            FROM assets.assets a JOIN organization.sites s ON s.id = a.site_id WHERE a.tenant_id = '{tenant}'
            """)).Should().Be("PEL-01@CAR-NORD,TOMB-01@CAR-SUD");
        (await ScalarAsync<string>($"""
            SELECT string_agg(m.kind || '=' || m.last_value, ',' ORDER BY a.code, m.kind)
            FROM assets.meters m JOIN assets.assets a ON a.id = m.asset_id WHERE m.tenant_id = '{tenant}'
            """)).Should().Be("Hours=9859.5,Hours=12321.0,Km=186506.4");
        (await ScalarAsync<long>($"SELECT count(*) FROM assets.meter_readings WHERE tenant_id = '{tenant}'")).Should().Be(6, "each meter: the initial reading and one more");
        (await ScalarAsync<long>($"""
            SELECT count(*) FROM audit.ledger_entries
            WHERE tenant_id = '{tenant}' AND event_type = 'assets.meter_reading_recorded.v1' AND payload::jsonb->>'actor' = '{DemoAssetsSeeder.Actor}'
            """)).Should().Be(6, "every reading, initial ones included, is the seed's, in the company's ledger");
    }

    [Fact]
    public async Task A_machine_on_a_site_the_company_does_not_have_stops_the_seed()
    {
        var tenant = await ProvisionAsync();

        var seeding = () => SeedAsync(tenant, [Demo[0]]);

        (await seeding.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*PEL-01*CAR-NORD*");
    }

    private static AssetInput Machine(string code, string type, IReadOnlyList<MeterInput> meters) =>
        new(code, $"Machine {code}", type, null, null, null, null, null, null, null, meters);

    private async Task<int> SeedAsync(Guid tenant, IReadOnlyList<DemoAsset> demo)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DemoAssetsSeeder>().SeedAsync(new TenantId(tenant), demo, Ct);
    }

    private async Task SiteAsync(Guid tenant, string code)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", TestTokens.ForTenant(tenant, "admin-1"), new
        {
            code,
            name = $"Carrière {code}",
            type = "quarry",
            address = new { line = "Route de Bizerte km 12", city = "Tunis", region = (string?)null, postalCode = "1000", country = "TN" },
            location = new { latitude = 36.8045, longitude = 10.0056 },
            boundary = (object?)null,
            timeZone = "Africa/Tunis",
            currency = (string?)null,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"d-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
