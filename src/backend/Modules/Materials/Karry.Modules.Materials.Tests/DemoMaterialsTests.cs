using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Modules.Materials.Application;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Materials.Tests;

/// <summary>The demo seed (US-P1-AST-04): materials with densities from 1 January through the real operations, ledgered, idempotent.</summary>
[Trait("Story", "US-P1-AST-04")]
public sealed class DemoMaterialsTests(MaterialsHost host) : IClassFixture<MaterialsHost>
{
    private static readonly MaterialInput[] Demo =
    [
        new("CAL-BRUT", "Calcaire abattu", "raw-rock", new DensityInput(2.56m, 1.60m, null, 0.02m), null),
        new("0/31.5", "Grave 0/31.5", "product", new DensityInput(null, 1.65m, 2.20m, 0.04m), new DateOnly(2001, 1, 1)),
        // Swell 2.65 / 1.70 − 1 = 0.5588… does not end: it must stay out of the ledger payload.
        new("GRA-BRUT", "Granite abattu", "raw-rock", new DensityInput(2.65m, 1.70m, null, 0.01m), null),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeds_materials_with_densities_from_january_first_in_the_ledger_once()
    {
        var tenant = await TenantAsync();

        (await SeedAsync(tenant)).Should().Be(3);
        (await SeedAsync(tenant)).Should().Be(0, "a company that has materials is left as it is");

        (await ScalarAsync<string>($"SELECT string_agg(code, ',' ORDER BY code) FROM materials.materials WHERE tenant_id = '{tenant}'")).Should().Be("0/31.5,CAL-BRUT,GRA-BRUT");
        (await ScalarAsync<long>($"SELECT count(*) FROM materials.materials WHERE tenant_id = '{tenant}' AND density_versions::text LIKE '%\"{DateTime.UtcNow.Year}-01-01\"%'"))
            .Should().Be(3, "all start on 1 January of the company's year, whatever the input says");
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'materials.material_created.v1' AND payload::jsonb->>'actor' = '{DemoMaterialsSeeder.Actor}'"))
            .Should().Be(3, "every material is the seed's, in the company's ledger");
    }

    private async Task<int> SeedAsync(Guid tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DemoMaterialsSeeder>().SeedAsync(new TenantId(tenant), Demo, Ct);
    }

    private async Task<Guid> TenantAsync()
    {
        var slug = $"d-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        request.Headers.Authorization = new("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
