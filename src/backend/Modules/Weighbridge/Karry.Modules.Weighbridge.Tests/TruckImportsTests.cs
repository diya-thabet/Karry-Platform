using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Weighbridge.Tests;

/// <summary>
/// The <c>trucks</c> import through the Imports pipeline (ADR-0027): a bad cell blocks the whole file; a plate is matched
/// however it is written (TC-P3-WB-016); empty cells keep what the truck has; an unverified truck stays unverified (its
/// review flag stays open); an archived truck is refused.
/// </summary>
[Trait("Story", "US-P3-SAL-04")]
public sealed class TruckImportsTests(WeighbridgeHost host) : IClassFixture<WeighbridgeHost>
{
    private static readonly string[] Headers =
        ["Plate", "Hauler", "Registered tare (kg)", "Tare date", "Tare method", "Legal payload (kg)", "Axles"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Yesterday => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Tomorrow => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private ImportDriver Imports => new(host.Client, host.RunImportsAsync);

    [Fact]
    [Trait("TestCase", "TC-P3-WB-016")]
    public async Task Bad_rows_import_nothing_then_the_fixed_file_registers_every_truck_once_however_plates_are_written()
    {
        var (tenant, token) = await TenantAsync();
        await HaulerAsync(token, "Transports Ben Salah");

        var job = await Imports.ImportAsync(tenant, "trucks", Workbook([
            ["123 TU 4567", "transports  ben salah", "14200", Yesterday, "weighed", "26000", "2+3"],
            ["124 tu 4568", "Inconnu SARL", null, null, null, null, null],            // row 3: unknown hauler
            ["125 TU 4569", null, "14200", null, "weighed", null, null],              // row 4: tare without its day
            ["126 TU 4570", null, "14200", Tomorrow, "weighed", null, null],          // row 5: tare in the future
            ["127 TU 4571", null, null, null, null, "lourd", "2+3"],                  // row 6: payload not a number
            ["128 TU 4572", null, null, null, null, null, "1+3"],                     // row 7: bad axles
            ["123-tu-4567", null, null, null, null, null, null],                      // row 8: same plate as row 2
        ]), Ct);

        (await Imports.IssuesAsync(tenant, job, "error", Ct)).Should().BeEquivalentTo([
            (2, "A2", "imports.key_duplicate"),
            (8, "A8", "imports.key_duplicate"),
            (3, "B3", "weighbridge.truck_hauler_unknown"),
            (4, "D4", "weighbridge.truck_tare_invalid"),
            (5, "D5", "weighbridge.truck_tare_invalid"),
            (6, "F6", "imports.number_invalid"),
            (7, "G7", "weighbridge.truck_axles_invalid"),
        ]);
        (await Imports.RequestCommitAsync(tenant, job, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await CountAsync(tenant)).Should().Be(0, "nothing is half-imported");

        List<string?[]> good = [["123 TU 4567", "Transports Ben Salah", "14200", Yesterday, "weighed", "26000", "2+3"], ["124tu4568", null, null, null, null, null, "3"]];
        var done = await Imports.ImportAndCommitAsync(tenant, "trucks", Workbook(good), Ct);
        done.GetProperty("created").GetInt32().Should().Be(2);

        var truck = await FindAsync(token, "tu 4567");
        truck.GetProperty("plate").GetString().Should().Be("123 TU 4567");
        truck.GetProperty("status").GetString().Should().Be("active");
        truck.GetProperty("tare").GetProperty("kg").GetDecimal().Should().Be(14_200m);
        (await FindAsync(token, "124 TU 4568")).GetProperty("plate").GetString().Should().Be("124TU4568", "the plate is kept as written");

        List<string?[]> rewritten = [["123-tu-4567", null, null, null, null, null, null], ["124 TU 4568", null, null, null, null, null, "3"]];
        var again = await Imports.ImportAndCommitAsync(tenant, "trucks", Workbook(rewritten), Ct);
        (again.GetProperty("created").GetInt32(), again.GetProperty("unchanged").GetInt32()).Should().Be((0, 2), "another way to write a plate is the same truck");
        (await LedgerCountAsync(tenant, "weighbridge.truck_created.v1")).Should().Be(2);
    }

    [Fact]
    public async Task Empty_cells_keep_the_tare_and_an_unverified_truck_stays_unverified_with_its_flag_open()
    {
        var (tenant, token) = await TenantAsync();
        await Imports.ImportAndCommitAsync(tenant, "trucks", Workbook([["200 TU 1000", null, "15000", Yesterday, "declared", "25000", "2+3"]]), Ct);
        var unknown = await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/trucks/unknown", token, new { plate = "201 TU 1001" });
        unknown.StatusCode.Should().Be(HttpStatusCode.Created);

        var changed = await Imports.ImportAndCommitAsync(tenant, "trucks", Workbook([
            ["200tu1000", null, null, null, null, "27000", null],
            ["201 TU 1001", null, "13900", Yesterday, "weighed", null, "3"],
        ]), Ct);
        changed.GetProperty("updated").GetInt32().Should().Be(2);

        var known = await FindAsync(token, "200TU1000");
        known.GetProperty("maxPayloadKg").GetDecimal().Should().Be(27_000m);
        known.GetProperty("tare").GetProperty("kg").GetDecimal().Should().Be(15_000m, "an empty tare keeps the registered one");
        known.GetProperty("axles").GetString().Should().Be("2+3");

        var met = await FindAsync(token, "201TU1001");
        met.GetProperty("tare").GetProperty("kg").GetDecimal().Should().Be(13_900m);
        met.GetProperty("status").GetString().Should().Be("unverified", "verifying is not the import's decision");
        (await ScalarAsync<long>($"SELECT count(*) FROM integrity.flags WHERE tenant_id = '{tenant}' AND rule = 'RULE-WB-09' AND closed_at IS NULL"))
            .Should().Be(1);
    }

    [Fact]
    public async Task An_archived_truck_is_refused_until_it_is_restored()
    {
        var (tenant, token) = await TenantAsync();
        await Imports.ImportAndCommitAsync(tenant, "trucks", Workbook([["300 TU 2000", null, null, null, null, null, "2"]]), Ct);
        var truck = await FindAsync(token, "300TU2000");
        (await SendAsync(HttpMethod.Post, $"/api/v1/weighbridge/trucks/{truck.GetProperty("id").GetGuid()}/archive", token)).StatusCode.Should().Be(HttpStatusCode.OK);

        var job = await Imports.ImportAsync(tenant, "trucks", Workbook([["300 TU 2000", null, null, null, null, null, "3"]]), Ct);

        (await Imports.IssuesAsync(tenant, job, "error", Ct)).Should().BeEquivalentTo([(2, "A2", "weighbridge.truck_archived")]);
    }

    private static byte[] Workbook(List<string?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Data");
        for (var c = 0; c < Headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = Headers[c];
        }

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] is { } value)
                {
                    sheet.Cell(r + 2, c + 1).Value = value; // text cells, as people type plates and dates
                }
            }
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private async Task<JsonElement> FindAsync(string token, string plate)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/v1/weighbridge/trucks?plate={Uri.EscapeDataString(plate)}", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(response)).GetProperty("items")[0];
    }

    private async Task HaulerAsync(string token, string name)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/weighbridge/haulers", token, new { name });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task<(Guid Tenant, string Token)> TenantAsync()
    {
        var slug = $"w-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tenant = (await Json(response)).GetProperty("id").GetGuid();
        return (tenant, TestTokens.ForTenant(tenant, "admin-1"));
    }

    private Task<long> CountAsync(Guid tenant) => ScalarAsync<long>($"SELECT count(*) FROM weighbridge.trucks WHERE tenant_id = '{tenant}'");

    private Task<long> LedgerCountAsync(Guid tenant, string eventType) =>
        ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = '{eventType}'");

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
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

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
}
