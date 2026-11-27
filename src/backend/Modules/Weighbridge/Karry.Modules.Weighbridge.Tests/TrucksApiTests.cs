using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Weighbridge.Tests;

/// <summary>
/// Trucks and haulers over HTTP (US-P3-SAL-04, ADR-0042): one truck per plate however it is typed; the desk finds a truck
/// by any part of its plate; an unknown plate becomes an unverified truck with a review flag that only someone else
/// verifies; every change in the company's ledger.
/// </summary>
[Trait("Story", "US-P3-SAL-04")]
public sealed class TrucksApiTests(WeighbridgeHost host) : IClassFixture<WeighbridgeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Yesterday => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object Truck(string plate, Guid? hauler = null) => new
    {
        plate,
        haulerId = hauler,
        tare = new { kg = 14_250m, on = Yesterday, method = "weighed" },
        maxPayloadKg = 26_000m,
        axles = "2+3",
    };

    [Fact]
    [Trait("TestCase", "TC-P3-WB-016")]
    public async Task Spaces_dashes_and_case_never_make_a_second_truck_and_the_desk_finds_any_part_of_the_plate()
    {
        var (tenant, token) = await TenantAsync();
        var hauler = await HaulerAsync(token, "Trans Nord");

        var created = await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks", token, Truck(" 123  tu 4567 ", hauler));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var truck = await Json(created);
        truck.GetProperty("plate").GetString().Should().Be("123 TU 4567");
        truck.GetProperty("plateKey").GetString().Should().Be("123TU4567");
        truck.GetProperty("status").GetString().Should().Be("active");
        truck.GetProperty("tare").GetProperty("kg").GetDecimal().Should().Be(14_250m);
        truck.GetProperty("tare").GetProperty("method").GetString().Should().Be("weighed");
        truck.GetProperty("axles").GetString().Should().Be("2+3");

        foreach (var same in new[] { "123TU4567", "123-tu-4567", "123 Tu 4567" })
        {
            var duplicate = await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks", token, Truck(same));
            duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict, $"'{same}' is the same plate");
            (await Json(duplicate)).GetProperty("code").GetString().Should().Be("weighbridge.truck_plate_taken");
            (await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks/unknown", token, new { plate = same })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks", token, Truck("88 TU 1234"));
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/weighbridge/trucks?plate=4567", token))).Select(t => t.GetProperty("plate").GetString())
            .Should().Equal("123 TU 4567");
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/weighbridge/trucks?plate=tu", token))).Should().HaveCount(2);
        (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/weighbridge/trucks?haulerId={hauler}", token))).Should().ContainSingle();
        (await CountAsync($"SELECT count(*) FROM weighbridge.trucks WHERE tenant_id = '{tenant}'")).Should().Be(2);
    }

    [Fact]
    [Trait("TestCase", "TC-P3-WB-017")]
    public async Task An_unknown_plate_at_the_desk_is_unverified_and_flagged_until_someone_else_verifies_it()
    {
        var (tenant, admin) = await TenantAsync();
        var weighmaster = TestTokens.ForTenant(tenant, WeighbridgeHost.Weighmaster);

        var created = await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks/unknown", weighmaster, new { plate = "tn 999 x" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var truck = await Json(created);
        truck.GetProperty("status").GetString().Should().Be("unverified");
        var id = truck.GetProperty("id").GetGuid();
        (await FlagAsync(tenant, id)).Should().Be(("open", null), "the review flag is raised with the truck, in one transaction");

        (await SendAsync(HttpMethod.Post, $"/api/v1/weighbridge/trucks/{id}/verify", weighmaster)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "the person who met the plate does not clear their own flag");

        var changed = await SendAsync(HttpMethod.Put, $"/api/v1/weighbridge/trucks/{id}", weighmaster, Truck("ignored"), truck.GetProperty("version").GetString());
        changed.StatusCode.Should().Be(HttpStatusCode.OK, "the details can be completed at the desk");
        (await Json(changed)).GetProperty("status").GetString().Should().Be("unverified", "completing details is not verifying");

        var verified = await SendAsync(HttpMethod.Post, $"/api/v1/weighbridge/trucks/{id}/verify", TestTokens.ForTenant(tenant, WeighbridgeHost.SiteManager));
        verified.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(verified)).GetProperty("status").GetString().Should().Be("active");
        (await FlagAsync(tenant, id)).Should().Be(("closed", "re-evaluated"));

        (await LedgerAsync(tenant)).Should().Be("weighbridge.truck_created.v1,weighbridge.truck_changed.v1,weighbridge.truck_verified.v1");
        _ = admin;
    }

    [Fact]
    public async Task A_registered_truck_raises_no_flag_and_archiving_and_restoring_it_is_ledgered()
    {
        var (tenant, token) = await TenantAsync();
        var id = (await Json(await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks", token, Truck("200 TU 1")))).GetProperty("id").GetGuid();
        (await CountAsync($"SELECT count(*) FROM integrity.flags WHERE tenant_id = '{tenant}'")).Should().Be(0);

        (await Json(await SendAsync(HttpMethod.Post, $"/api/v1/weighbridge/trucks/{id}/archive", token))).GetProperty("status").GetString().Should().Be("archived");
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/weighbridge/trucks?status=active", token))).Should().BeEmpty();
        (await Json(await SendAsync(HttpMethod.Post, $"/api/v1/weighbridge/trucks/{id}/restore", token))).GetProperty("status").GetString().Should().Be("active");
        (await LedgerAsync(tenant)).Should().Be("weighbridge.truck_created.v1,weighbridge.truck_archived.v1,weighbridge.truck_restored.v1");
    }

    [Theory]
    [InlineData("X", null, null, null, "weighbridge.truck_plate_invalid")]
    [InlineData("12 TU 5", -5.0, null, null, "weighbridge.truck_tare_invalid")]
    [InlineData("12 TU 5", 14250.5, null, null, "weighbridge.truck_tare_invalid")]
    [InlineData("12 TU 5", null, 0.0, null, "weighbridge.truck_payload_invalid")]
    [InlineData("12 TU 5", null, null, "2+3+4+5+6", "weighbridge.truck_axles_invalid")]
    [InlineData("12 TU 5", null, null, "1", "weighbridge.truck_axles_invalid")]
    public async Task Invalid_details_are_refused_with_their_code(string plate, double? tareKg, double? payloadKg, string? axles, string code)
    {
        var (_, token) = await TenantAsync();
        var body = new
        {
            plate,
            tare = tareKg is { } kg ? new { kg = (decimal)kg, on = Yesterday, method = "declared" } : null,
            maxPayloadKg = (decimal?)payloadKg,
            axles,
        };

        var refused = await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks", token, body);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(refused)).GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task A_tare_established_in_the_future_or_without_its_method_is_refused()
    {
        var (_, token) = await TenantAsync();
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        (await Json(await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks", token, new { plate = "1 TU 1", tare = new { kg = 14000m, on = tomorrow, method = "weighed" } })))
            .GetProperty("code").GetString().Should().Be("weighbridge.truck_tare_invalid");
        (await Json(await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks", token, new { plate = "1 TU 1", tare = new { kg = 14000m, on = Yesterday, method = "guessed" } })))
            .GetProperty("code").GetString().Should().Be("weighbridge.truck_tare_invalid");
    }

    [Fact]
    public async Task Hauler_names_are_unique_however_written_and_an_archived_hauler_takes_no_new_truck()
    {
        var (_, token) = await TenantAsync();
        var created = await Json(await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/haulers", token, new { name = "Trans-Nord", phone = "+216 71 000 000" }));
        (await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/haulers", token, new { name = "trans nord" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var id = created.GetProperty("id").GetGuid();
        (await SendAsync(HttpMethod.Put, $"/api/v1/weighbridge/haulers/{id}", token, new { name = "Trans-Nord", status = "archived" }, created.GetProperty("version").GetString()))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/weighbridge/haulers", token))).Should().BeEmpty();
        var refused = await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks/unknown", token, new { plate = "5 TU 5", haulerId = id });
        (await Json(refused)).GetProperty("code").GetString().Should().Be("weighbridge.truck_hauler_unknown");
    }

    [Fact]
    public async Task Another_company_sees_none_of_my_trucks_and_may_reuse_the_plate()
    {
        var (_, mine) = await TenantAsync();
        var id = (await Json(await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks", mine, Truck("777 TU 7")))).GetProperty("id").GetGuid();
        var (_, theirs) = await TenantAsync();

        (await SendAsync(HttpMethod.Get, $"/api/v1/weighbridge/trucks/{id}", theirs)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Items(await SendAsync(HttpMethod.Get, "/api/v1/weighbridge/trucks?plate=777", theirs))).Should().BeEmpty();
        (await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks", theirs, Truck("777 TU 7"))).StatusCode.Should().Be(HttpStatusCode.Created, "plates are unique per company");
    }

    private async Task<Guid> HaulerAsync(string token, string name)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/haulers", token, new { name });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<(string Status, string? Reason)> FlagAsync(Guid tenant, Guid truck)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT closed_at IS NULL, close_reason FROM integrity.flags WHERE tenant_id = '{tenant}' AND rule = 'RULE-WB-09' AND subject_type = 'truck' AND subject_id = '{truck}'",
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue("the flag exists");
        var open = reader.GetBoolean(0);
        var reason = reader.IsDBNull(1) ? null : reader.GetString(1);
        (await reader.ReadAsync(Ct)).Should().BeFalse("one flag per truck");
        return (open ? "open" : "closed", reason);
    }

    private async Task<(Guid Tenant, string Token)> TenantAsync()
    {
        var slug = $"w-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await Json(response)).GetProperty("id").GetGuid();
        return (tenant, TestTokens.ForTenant(tenant));
    }

    private async Task<string> LedgerAsync(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT string_agg(event_type, ',' ORDER BY seq) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type LIKE 'weighbridge.%'", connection);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

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

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<IReadOnlyList<JsonElement>> Items(HttpResponseMessage response) =>
        [.. (await Json(response)).GetProperty("items").EnumerateArray()];
}
