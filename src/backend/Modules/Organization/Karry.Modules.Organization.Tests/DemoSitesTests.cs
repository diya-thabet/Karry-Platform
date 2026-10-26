using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Modules.Organization.Application;
using Karry.Modules.Organization.Domain;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Organization.Tests;

/// <summary>The demo seed (US-P1-ORG-01/02): sites, shifts and holidays through the real operations, ledgered, idempotent.</summary>
[Trait("Story", "US-P1-ORG-02")]
public sealed class DemoSitesTests(OrganizationHost host) : IClassFixture<OrganizationHost>
{
    private static readonly string[] Week = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static readonly DemoSite[] Demo =
    [
        new(new SiteInput("CAR-NORD", "Carrière Nord", "quarry", new AddressInput("Route de Mornag", "Mornag", null, null, "TN"), new LocationInput(36.609, 10.32),
                [[[10.315, 36.605], [10.325, 36.605], [10.325, 36.613], [10.315, 36.613], [10.315, 36.605]]], "Africa/Tunis", null),
            new ShiftPatternInput([new ShiftInput("JOUR", "Jour", "06:00", "18:00", Week), new ShiftInput("NUIT", "Nuit", "18:00", "06:00", Week)]),
            [new DemoHoliday(1, 1, "Jour de l’an"), new DemoHoliday(12, 17, "Fête de la révolution")]),
        new(new SiteInput("LAV-SUD", "Laverie Sud", "sand-washing", new AddressInput("Zone industrielle", "Zaghouan", null, null, "TN"), new LocationInput(36.402, 10.143),
                null, "Africa/Tunis", null),
            new ShiftPatternInput([new ShiftInput("M", "Matin", "06:00", "14:00", Week[..6]), new ShiftInput("A", "Après-midi", "14:00", "22:00", Week[..6]),
                new ShiftInput("N", "Nuit", "22:00", "06:00", Week[..6])]),
            [new DemoHoliday(1, 1, "Jour de l’an")]),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeds_sites_with_their_patterns_and_coming_holidays_in_the_ledger_once()
    {
        var tenant = await TenantAsync();

        (await SeedAsync(tenant)).Should().Be(2);
        (await SeedAsync(tenant)).Should().Be(0, "a company that has sites is left as it is");

        var today = ShiftCalendar.Today("Africa/Tunis", DateTimeOffset.UtcNow);
        (await ScalarAsync<string>($"SELECT string_agg(code, ',' ORDER BY code) FROM organization.sites WHERE tenant_id = '{tenant}'")).Should().Be("CAR-NORD,LAV-SUD");
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.sites WHERE tenant_id = '{tenant}' AND boundary IS NOT NULL")).Should().Be(1);
        (await ScalarAsync<DateOnly>($"SELECT min(effective_from) FROM organization.shift_patterns WHERE tenant_id = '{tenant}'")).Should().Be(new DateOnly(today.Year, 1, 1));
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.non_working_days WHERE tenant_id = '{tenant}' AND date <= '{today:yyyy-MM-dd}'"))
            .Should().Be(0, "only coming holidays");
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.non_working_days WHERE tenant_id = '{tenant}' AND date = '{today.Year + 1}-01-01'"))
            .Should().Be(2, "next New Year's Day on both sites");
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_instances i JOIN organization.sites s ON s.id = i.site_id WHERE s.code = 'LAV-SUD' AND s.tenant_id = '{tenant}' AND extract(isodow FROM i.date) = 7"))
            .Should().Be(0, "the plant stops on Sundays");
        (await ScalarAsync<long>($"SELECT count(*) FROM organization.shift_instances WHERE tenant_id = '{tenant}'")).Should().BeGreaterThan(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type LIKE 'organization.%' AND payload::jsonb->>'actor' IS DISTINCT FROM '{DemoSitesSeeder.Actor}'"))
            .Should().Be(0, "every change is the seed's, in the company's ledger");
    }

    private async Task<int> SeedAsync(Guid tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DemoSitesSeeder>().SeedAsync(new TenantId(tenant), Demo, Ct);
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
