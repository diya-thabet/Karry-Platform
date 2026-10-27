using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Materials.Tests;

/// <summary>
/// The material import through the Imports pipeline (US-P1-ORG-03, ADR-0027): errors on their cells block the file; a
/// new code is created with its densities; the same file again changes nothing (TC-P1-ORG-002); new densities of a
/// known material need a future date (past quantities keep theirs), and an archived material is refused.
/// </summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class MaterialImportsTests(MaterialsHost host) : IClassFixture<MaterialsHost>
{
    private static readonly string[] Headers =
        ["Code", "Name", "Kind", "Bank density (t/m³)", "Loose density (t/m³)", "Compacted density (t/m³)", "Moisture (fraction)", "Densities from"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ImportDriver Imports => new(host.Client, host.RunImportsAsync);

    [Fact]
    public async Task Errors_are_reported_on_their_cells_and_block_the_file_and_the_fixed_file_creates_the_materials()
    {
        var tenant = await ProvisionAsync();
        Row[] rows =
        [
            Gravel("g-8/16"),
            Gravel("G-0/4") with { Kind = "sand" },
            Gravel("G-4/8") with { Loose = "5" },
            Gravel("G-16/25") with { Bank = null, Loose = null },
            Gravel("TV-0/31") with { Moisture = null },
            Gravel("TV-0/80") with { Bank = "lourd" },
            Gravel("ROC") with { Kind = "raw-rock", Loose = "2.6" },
            Gravel("!bad"),
        ];

        var job = await Imports.ImportAsync(tenant, "materials", Workbook(rows), Ct);

        (await Imports.IssuesAsync(tenant, job, "error", Ct)).Should().BeEquivalentTo([
            (3, "C3", "materials.kind_invalid"),
            (4, "E4", "materials.density_invalid"),
            (5, "D5", "materials.density_required"),
            (6, "G6", "materials.moisture_invalid"),
            (7, "D7", "imports.number_invalid"),
            (8, "E8", "materials.swell_invalid"),
            (9, "A9", "materials.code_invalid"),
        ]);
        (await Imports.PreviewAsync(tenant, job, Ct)).Should().ContainSingle().Which.Should().Be(("G-8/16", "create"));
        (await Imports.RequestCommitAsync(tenant, job, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await CountAsync(tenant)).Should().Be(0);

        var done = await Imports.ImportAndCommitAsync(tenant, "materials", Workbook(Gravel("G-8/16"), Gravel("ROC") with { Kind = "Raw-Rock", Loose = null }), Ct);

        done.GetProperty("created").GetInt32().Should().Be(2);
        var material = await MaterialAsync(tenant, "G-8/16");
        material.GetProperty("densities").EnumerateArray().Should().ContainSingle().Which.GetProperty("loose").GetDecimal().Should().Be(1.6m);
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'materials.material_created.v1'")).Should().Be(2);
    }

    [Fact]
    public async Task The_same_file_again_changes_nothing_new_densities_need_a_future_date_and_an_archived_material_is_refused()
    {
        var tenant = await ProvisionAsync();
        Row[] rows = [Gravel("G-8/16"), Gravel("G-0/4")];
        await Imports.ImportAndCommitAsync(tenant, "materials", Workbook(rows), Ct);

        var again = await Imports.CommitAsync(tenant, await Imports.ImportAsync(tenant, "materials", Workbook(rows), Ct), Ct);
        (again.GetProperty("created").GetInt32(), again.GetProperty("unchanged").GetInt32()).Should().Be((0, 2), "TC-P1-ORG-002");
        var withoutDensities = await Imports.ImportAsync(tenant, "materials", Workbook(rows[0] with { Bank = null, Loose = null, Moisture = null }), Ct);
        (await Imports.PreviewAsync(tenant, withoutDensities, Ct)).Should().Equal(("G-8/16", "unchanged"));

        var today = await Imports.ImportAsync(tenant, "materials", Workbook(rows[0] with { Loose = "1.65" }), Ct);
        (await Imports.IssuesAsync(tenant, today, "error", Ct)).Should().ContainSingle().Which.Should().Be((2, "H2", "materials.date_in_past"));

        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var changed = await Imports.ImportAndCommitAsync(tenant, "materials", Workbook(rows[0] with { Name = "Gravier 8/16 lavé", Loose = "1.65", From = from }, rows[1]), Ct);
        (changed.GetProperty("updated").GetInt32(), changed.GetProperty("unchanged").GetInt32()).Should().Be((1, 1));
        var material = await MaterialAsync(tenant, "G-8/16");
        material.GetProperty("name").GetString().Should().Be("Gravier 8/16 lavé");
        material.GetProperty("densities").EnumerateArray().Select(v => v.GetProperty("loose").GetDecimal()).Should().Equal(1.6m, 1.65m);

        (await SendAsync(HttpMethod.Post, $"/api/v1/materials/{material.GetProperty("id").GetGuid()}/archive", tenant)).IsSuccessStatusCode.Should().BeTrue();
        var archived = await Imports.ImportAsync(tenant, "materials", Workbook(rows), Ct);
        (await Imports.IssuesAsync(tenant, archived, "error", Ct)).Should().ContainSingle().Which.Should().Be((2, "A2", "materials.material_archived"));
    }

    private static Row Gravel(string code) => new(code, $"Gravier {code}", "product", "2.65", "1.6", null, "0.03", null);

    private sealed record Row(string? Code, string? Name, string? Kind, string? Bank, string? Loose, string? Compacted, string? Moisture, string? From)
    {
        public string?[] Cells => [Code, Name, Kind, Bank, Loose, Compacted, Moisture, From];
    }

    private static byte[] Workbook(params Row[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Data");
        for (var c = 0; c < Headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = Headers[c];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            var cells = rows[r].Cells;
            for (var c = 0; c < cells.Length; c++)
            {
                if (cells[c] is { } value)
                {
                    sheet.Cell(r + 2, c + 1).Value = value;
                }
            }
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private async Task<JsonElement> MaterialAsync(Guid tenant, string code)
    {
        var page = await (await SendAsync(HttpMethod.Get, "/api/v1/materials?includeArchived=true", tenant)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        return page.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code);
    }

    private Task<long> CountAsync(Guid tenant) => ScalarAsync<long>($"SELECT count(*) FROM materials.materials WHERE tenant_id = '{tenant}'");

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"i-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", null, new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? tenant, object? body = null)
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

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenant is { } t ? TestTokens.ForTenant(t, "admin-1") : TestTokens.ForPlatformOperator());
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
