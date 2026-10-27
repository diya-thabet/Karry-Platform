using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Application;
using Karry.Modules.Assets.Domain;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Assets.Tests;

/// <summary>
/// Meters over HTTP (US-P1-AST-03): an asset gets its type's meters, readings only go forward and are only ever added,
/// each one in the company's ledger (TC-P1-AST-002); a failed meter takes no reading.
/// </summary>
[Trait("Story", "US-P1-AST-03")]
public sealed class MetersApiTests(AssetsHost host) : IClassFixture<AssetsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_asset_gets_its_types_meters_from_zero_installed_on_its_acquisition_date()
    {
        var tenant = await ProvisionAsync();
        var site = await SiteAsync(tenant);

        var truck = await RegisterAsync(tenant, site, "DUM-01", "haul-truck");
        var meters = await Items(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{truck}/meters", Admin(tenant)));

        meters.Select(m => (m.GetProperty("kind").GetString(), m.GetProperty("unit").GetString())).Should().Equal(("hours", "h"), ("km", "km"));
        meters.Should().AllSatisfy(m =>
        {
            m.GetProperty("initialReading").GetDecimal().Should().Be(0m);
            m.GetProperty("installedOn").GetString().Should().Be("2021-03-15");
            m.GetProperty("status").GetString().Should().Be("ok");
            m.GetProperty("offset").GetDecimal().Should().Be(0m);
        });
        (await LedgerCountAsync(tenant, "assets.meter_added.v1")).Should().Be(2);
        (await LedgerCountAsync(tenant, "assets.meter_reading_recorded.v1")).Should().Be(2, "the initial reading opens each history");

        var other = await RegisterAsync(tenant, site, "DIV-01", "other");
        (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{other}/meters", Admin(tenant)))).Should().BeEmpty();
    }

    [Fact]
    public async Task An_asset_registered_with_its_own_meters_gets_those_and_invalid_ones_stop_the_registration()
    {
        var tenant = await ProvisionAsync();
        var site = await SiteAsync(tenant);

        var used = await RegisterAsync(tenant, site, "PEL-01", "excavator", [new { kind = "hours", initialReading = 12450.5m, installedOn = "2024-06-01" }]);
        var meter = (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{used}/meters", Admin(tenant)))).Single();
        meter.GetProperty("lastValue").GetDecimal().Should().Be(12450.5m);
        meter.GetProperty("lastReadAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));

        foreach (var (input, error) in new (object Meter, string Error)[]
        {
            (new { kind = "litres", initialReading = 0m, installedOn = "2024-06-01" }, "assets.meter_kind_invalid"),
            (new { kind = "hours", initialReading = 1.25m, installedOn = "2024-06-01" }, "assets.reading_invalid"),
            (new { kind = "hours", initialReading = -1m, installedOn = "2024-06-01" }, "assets.reading_invalid"),
            (new { kind = "km", initialReading = 0m, installedOn = "2999-01-01" }, "assets.installed_on_invalid"),
        })
        {
            var response = await SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), Asset(site, "PEL-02", "excavator", [input]));
            (await Json(response)).GetProperty("code").GetString().Should().Be(error);
        }

        (await ScalarAsync<long>($"SELECT count(*) FROM assets.assets WHERE tenant_id = '{tenant}'")).Should().Be(1, "nothing of a refused registration is saved");
    }

    [Fact]
    public async Task Readings_only_go_forward_and_are_listed_newest_first_page_by_page()
    {
        var tenant = await ProvisionAsync();
        var asset = await RegisterAsync(tenant, await SiteAsync(tenant), "PEL-01", "excavator");
        var meter = await MeterAsync(tenant, asset);
        var monday = new DateTimeOffset(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);

        foreach (var (value, at) in new[] { (100m, monday), (108.5m, monday.AddHours(12)), (108.5m, monday.AddDays(1)) })
        {
            (await Record(tenant, asset, meter, value, at)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        (await Error(await Record(tenant, asset, meter, 108.4m, monday.AddDays(2)))).Should().Be("assets.reading_decreased");
        (await Error(await Record(tenant, asset, meter, 120m, monday.AddHours(13)))).Should().Be("assets.reading_before_last");
        (await Error(await Record(tenant, asset, meter, 120m, DateTimeOffset.UtcNow.AddHours(1)))).Should().Be("assets.reading_in_future");
        (await Error(await Record(tenant, asset, meter, 120.05m, monday.AddDays(2)))).Should().Be("assets.reading_invalid");

        var first = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/meters/{meter}/readings?limit=2", Admin(tenant)));
        first.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("value").GetDecimal()).Should().Equal(108.5m, 108.5m);
        var next = first.GetProperty("nextCursor").GetGuid();
        var rest = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/meters/{meter}/readings?limit=2&cursor={next}", Admin(tenant)));
        rest.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("value").GetDecimal()).Should().Equal(100m, 0m);
        rest.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        var current = (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/meters", Admin(tenant)))).Single();
        current.GetProperty("lastValue").GetDecimal().Should().Be(108.5m);
        (await LedgerCountAsync(tenant, "assets.meter_reading_recorded.v1")).Should().Be(4, "the initial reading and three accepted ones; refused ones leave no trace");
    }

    [Fact]
    public async Task Meter_history_cannot_be_updated_or_deleted_through_the_api_or_the_database()
    {
        // TC-P1-AST-002
        var tenant = await ProvisionAsync();
        var asset = await RegisterAsync(tenant, await SiteAsync(tenant), "PEL-01", "excavator");
        var meter = await MeterAsync(tenant, asset);
        var reading = (await Json(await Record(tenant, asset, meter, 250m, DateTimeOffset.UtcNow.AddMinutes(-5)))).GetProperty("id").GetGuid();

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var response = await SendAsync(method, $"/api/v1/assets/{asset}/meters/{meter}/readings/{reading}", Admin(tenant), new { value = 1m });
            response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        }

        foreach (var sql in new[]
        {
            $"UPDATE assets.meter_readings SET value = 1 WHERE id = '{reading}'",
            $"DELETE FROM assets.meter_readings WHERE id = '{reading}'",
        })
        {
            var asApp = () => InTenantAsync(host.Database.App, tenant, sql);
            (await asApp.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            var asOwner = () => InTenantAsync(host.Database.Owner, tenant, sql);
            (await asOwner.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        }

        (await ScalarAsync<decimal>($"SELECT value FROM assets.meter_readings WHERE id = '{reading}'")).Should().Be(250m);
        (await ScalarAsync<string>($"SELECT payload::jsonb->>'value' FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND payload::jsonb->>'readingId' = '{reading}'"))
            .Should().Be("250", "the reading is in the company's ledger");
    }

    [Fact]
    public async Task A_failed_meter_takes_no_reading_until_it_is_ok_and_replacement_waits_for_phase_2()
    {
        var tenant = await ProvisionAsync();
        var asset = await RegisterAsync(tenant, await SiteAsync(tenant), "PEL-01", "excavator");
        var meter = await MeterAsync(tenant, asset);
        var status = $"/api/v1/assets/{asset}/meters/{meter}/status";

        (await Json(await SendAsync(HttpMethod.Post, status, Admin(tenant), new { status = "failed" }))).GetProperty("status").GetString().Should().Be("failed");
        (await Error(await Record(tenant, asset, meter, 10m, DateTimeOffset.UtcNow))).Should().Be("assets.meter_not_counting");
        await SendAsync(HttpMethod.Post, status, Admin(tenant), new { status = "ok" });
        (await Record(tenant, asset, meter, 10m, DateTimeOffset.UtcNow)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await LedgerCountAsync(tenant, "assets.meter_status_changed.v1")).Should().Be(2);

        var replaced = await SendAsync(HttpMethod.Post, status, Admin(tenant), new { status = "replaced" });
        replaced.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Error(replaced)).Should().Be("assets.meter_replacement_unavailable");
    }

    [Fact]
    public async Task A_meter_is_added_later_and_a_disposed_asset_takes_neither_meters_nor_readings()
    {
        var tenant = await ProvisionAsync();
        var asset = await RegisterAsync(tenant, await SiteAsync(tenant), "GEN-01", "other");

        var added = await SendAsync(HttpMethod.Post, $"/api/v1/assets/{asset}/meters", Admin(tenant), new { kind = "hours", initialReading = 3.5m });
        added.StatusCode.Should().Be(HttpStatusCode.Created);
        var meter = (await Json(added)).GetProperty("id").GetGuid();

        await SendAsync(HttpMethod.Post, $"/api/v1/assets/{asset}/status", Admin(tenant), new { status = "disposed" });
        (await Error(await SendAsync(HttpMethod.Post, $"/api/v1/assets/{asset}/meters", Admin(tenant), new { kind = "km" }))).Should().Be("assets.asset_disposed");
        (await Error(await Record(tenant, asset, meter, 10m, DateTimeOffset.UtcNow))).Should().Be("assets.asset_disposed");
        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/meters", Admin(tenant))).StatusCode.Should().Be(HttpStatusCode.OK, "its history stays readable");
    }

    [Fact]
    public async Task A_meter_is_reached_only_through_its_own_asset_on_the_callers_sites()
    {
        var tenant = await ProvisionAsync();
        var nord = await SiteAsync(tenant, "nord");
        var sud = await SiteAsync(tenant, "sud");
        var onNord = await RegisterAsync(tenant, nord, "PEL-01", "excavator");
        var onSud = await RegisterAsync(tenant, sud, "PEL-02", "excavator");
        var sudMeter = await MeterAsync(tenant, onSud);
        host.SiteOf[AssetsHost.SiteManager] = nord;
        var manager = TestTokens.ForTenant(tenant, AssetsHost.SiteManager);

        (await Error(await Record(tenant, onNord, sudMeter, 10m, DateTimeOffset.UtcNow))).Should().Be("assets.meter_not_found", "the meter belongs to another asset");
        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/{onSud}/meters", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/{onSud}/meters/{sudMeter}/readings", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, $"/api/v1/assets/{onSud}/meters/{sudMeter}/readings", manager, new { value = 10m })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Of_two_readings_saved_at_once_on_one_meter_the_second_is_refused()
    {
        var tenant = await ProvisionAsync();
        var asset = await RegisterAsync(tenant, await SiteAsync(tenant), "PEL-01", "excavator");
        var meter = new MeterId(await MeterAsync(tenant, asset));
        await using var first = Scope(tenant);
        await using var second = Scope(tenant);
        var clock = TimeProvider.System;

        var a = (await first.ServiceProvider.GetRequiredService<IMeterStore>().FindAsync(meter, Ct))!;
        var b = (await second.ServiceProvider.GetRequiredService<IMeterStore>().FindAsync(meter, Ct))!;
        a.Record(clock, 20m, clock.GetUtcNow(), "a").IsSuccess.Should().BeTrue();
        b.Record(clock, 10m, clock.GetUtcNow(), "b").IsSuccess.Should().BeTrue("each saw the initial reading only");

        (await first.ServiceProvider.GetRequiredService<IMeterStore>().TrySaveAsync(a, Ct)).Should().BeTrue();
        (await second.ServiceProvider.GetRequiredService<IMeterStore>().TrySaveAsync(b, Ct)).Should().BeFalse("10 after 20 would make the meter go back");
        (await ScalarAsync<long>($"SELECT count(*) FROM assets.meter_readings WHERE meter_id = '{meter}'")).Should().Be(2);
    }

    private AsyncServiceScope Scope(Guid tenant)
    {
        var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        return scope;
    }

    private static string Admin(Guid tenant) => TestTokens.ForTenant(tenant, "admin-1");

    private static object Asset(Guid site, string code, string type, object[]? meters = null) => new
    {
        code,
        name = $"Machine {code}",
        type,
        make = "Caterpillar",
        model = "336",
        year = 2021,
        acquiredOn = "2021-03-15",
        siteId = site,
        meters,
    };

    private Task<HttpResponseMessage> Record(Guid tenant, Guid asset, Guid meter, decimal value, DateTimeOffset readAt) =>
        SendAsync(HttpMethod.Post, $"/api/v1/assets/{asset}/meters/{meter}/readings", Admin(tenant), new { value, readAt });

    private async Task<Guid> RegisterAsync(Guid tenant, Guid site, string code, string type, object[]? meters = null)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), Asset(site, code, type, meters));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> MeterAsync(Guid tenant, Guid asset) =>
        (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/meters", Admin(tenant))))[0].GetProperty("id").GetGuid();

    private async Task<Guid> SiteAsync(Guid tenant, string code = "nord")
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/organization/sites", Admin(tenant), new
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
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"m-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method != HttpMethod.Get)
        {
            request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<string?> Error(HttpResponseMessage response) => (await Json(response)).GetProperty("code").GetString();

    private static async Task<IReadOnlyList<JsonElement>> Items(HttpResponseMessage response) =>
        [.. (await Json(response)).GetProperty("items").EnumerateArray()];

    private Task<long> LedgerCountAsync(Guid tenant, string eventType) =>
        ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = '{eventType}'");

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task InTenantAsync(string connectionString, Guid tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenant}', true)", connection, transaction))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
