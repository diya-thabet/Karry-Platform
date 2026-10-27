using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Karry.BuildingBlocks.Idempotency;
using Karry.Modules.Assets.Domain;
using Karry.Testing;
using QRCoder;

namespace Karry.Modules.Assets.Tests;

/// <summary>
/// QR labels (US-P1-AST-02 AC2, ADR-0026): a random token that resolves only inside its company and the caller's sites
/// (TC-P1-AST-001), reissued on demand, printed on A4 sheets of 3 × 8 labels.
/// </summary>
[Trait("Story", "US-P1-AST-02")]
public sealed partial class LabelsApiTests(AssetsHost host) : IClassFixture<AssetsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_label_token_is_random_base32_and_parsed_with_or_without_its_prefix()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => AssetLabels.NewToken()).ToList();

        tokens.Should().OnlyHaveUniqueItems().And.AllSatisfy(t => t.Should().MatchRegex("^[A-Z2-7]{26}$"));
        tokens.SelectMany(t => t).Distinct().Should().HaveCount(32, "every base32 character is used");
        AssetLabels.Token($" kry1:{tokens[0].ToLowerInvariant()} ").Should().Be(tokens[0]);
        AssetLabels.Token(tokens[0]).Should().Be(tokens[0]);
        AssetLabels.Token("KRY1:" + tokens[0][..25]).Should().BeNull();
        AssetLabels.Token("KRY1:" + tokens[0][..25] + "1").Should().BeNull("1 is not base32");
        AssetLabels.Token(null).Should().BeNull();
    }

    [Fact]
    public async Task A_label_resolves_in_its_company_and_is_not_found_in_another()
    {
        // TC-P1-AST-001
        var a = await ProvisionAsync();
        var b = await ProvisionAsync();
        var asset = await Json(await RegisterAsync(a, await SiteAsync(a, "nord"), "PEL-01"));
        var label = asset.GetProperty("label").GetString()!;
        label.Should().MatchRegex("^KRY1:[A-Z2-7]{26}$").And.NotContain(asset.GetProperty("id").GetString()!);

        var resolved = await SendAsync(HttpMethod.Get, $"/api/v1/assets/labels/{Uri.EscapeDataString(label)}", Admin(a));
        (await Json(resolved)).GetProperty("code").GetString().Should().Be("PEL-01");

        var elsewhere = await SendAsync(HttpMethod.Get, $"/api/v1/assets/labels/{Uri.EscapeDataString(label)}", Admin(b));
        elsewhere.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await elsewhere.Content.ReadAsStringAsync(Ct);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be("assets.label_unknown");
        body.Should().NotContainAny("PEL-01", asset.GetProperty("id").GetString()!);

        foreach (var garbage in new[] { "KRY1:AAAAAAAAAAAAAAAAAAAAAAAAAA", "hello", asset.GetProperty("id").GetString()! })
        {
            (await SendAsync(HttpMethod.Get, $"/api/v1/assets/labels/{Uri.EscapeDataString(garbage)}", Admin(a))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task A_label_of_another_site_is_unknown_to_a_site_role_and_a_reissued_label_stops_resolving()
    {
        var tenant = await ProvisionAsync();
        var nord = await SiteAsync(tenant, "nord");
        var sud = await SiteAsync(tenant, "sud");
        var onSud = await Json(await RegisterAsync(tenant, sud, "PEL-02"));
        host.SiteOf[AssetsHost.Operator] = nord;
        var old = onSud.GetProperty("label").GetString()!;

        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/labels/{old}", TestTokens.ForTenant(tenant, AssetsHost.Operator))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var reissued = await Json(await SendAsync(HttpMethod.Post, $"/api/v1/assets/{onSud.GetProperty("id").GetGuid()}/label", Admin(tenant)));
        var fresh = reissued.GetProperty("label").GetString()!;
        fresh.Should().NotBe(old);
        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/labels/{old}", Admin(tenant))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/labels/{fresh}", Admin(tenant))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LedgerPayloadsAsync(tenant, "assets.asset_label_reissued.v1")).Should().ContainSingle().Which.Should().NotContainAny(old[5..], fresh[5..]);
    }

    [Fact]
    public async Task The_sheet_is_a_pdf_of_24_labels_per_a4_page_with_each_qr_code_and_code()
    {
        var tenant = await ProvisionAsync();
        var site = await SiteAsync(tenant, "nord");
        var assets = new List<JsonElement>();
        for (var i = 1; i <= 3; i++)
        {
            assets.Add(await Json(await RegisterAsync(tenant, site, $"PEL-{i:00}", plate: i == 1 ? "123 TU 4567" : null)));
        }

        var response = await SendAsync(HttpMethod.Post, "/api/v1/assets/labels/sheet", Admin(tenant),
            new { assetIds = assets.Select(a => a.GetProperty("id").GetGuid()), skip = 22 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var pdf = Pdf.Read(await response.Content.ReadAsByteArrayAsync(Ct));
        pdf.Pages.Should().HaveCount(2, "skipping 22 labels leaves 2 on the first sheet, the third goes on a second one");
        pdf.Pages[0].Should().Contain("(PEL-01) Tj").And.Contain("(PEL-02) Tj").And.Contain("(123 TU 4567) Tj").And.NotContain("PEL-03");
        pdf.Pages[1].Should().Contain("(PEL-03) Tj");
        RectangleCount(pdf.Pages[1]).Should().Be(DarkRuns(assets[2].GetProperty("label").GetString()!), "the third label's QR code is drawn module run by module run");
    }

    [Fact]
    public async Task A_sheet_is_refused_when_empty_too_long_or_with_an_asset_out_of_reach()
    {
        var tenant = await ProvisionAsync();
        var nord = await SiteAsync(tenant, "nord");
        var sud = await SiteAsync(tenant, "sud");
        var onNord = (await Json(await RegisterAsync(tenant, nord, "PEL-01"))).GetProperty("id").GetGuid();
        var onSud = (await Json(await RegisterAsync(tenant, sud, "PEL-02"))).GetProperty("id").GetGuid();
        host.SiteOf[AssetsHost.SiteManager] = nord;

        foreach (var body in new object[] { new { assetIds = Array.Empty<Guid>() }, new { assetIds = new[] { onNord }, skip = 24 }, new { assetIds = Enumerable.Repeat(onNord, 241) } })
        {
            (await Error(await SendAsync(HttpMethod.Post, "/api/v1/assets/labels/sheet", Admin(tenant), body))).Should().Be("assets.label_sheet_invalid");
        }

        var manager = TestTokens.ForTenant(tenant, AssetsHost.SiteManager);
        (await SendAsync(HttpMethod.Post, "/api/v1/assets/labels/sheet", manager, new { assetIds = new[] { onNord, onSud } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, "/api/v1/assets/labels/sheet", manager, new { assetIds = new[] { onNord } })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static int RectangleCount(string content) => RectanglePattern().Count(content);

    /// <summary>Runs of dark modules in the QR code of <paramref name="label"/>, as the sheet draws them.</summary>
    private static int DarkRuns(string label)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(label, QRCodeGenerator.ECCLevel.M);
        return data.ModuleMatrix.Sum(row => Enumerable.Range(0, row.Length).Count(i => row[i] && (i == 0 || !row[i - 1])));
    }

    private static string Admin(Guid tenant) => TestTokens.ForTenant(tenant, "admin-1");

    private Task<HttpResponseMessage> RegisterAsync(Guid tenant, Guid site, string code, string? plate = null) =>
        SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), new { code, name = $"Pelle {code}", type = "excavator", plate, siteId = site });

    private async Task<Guid> SiteAsync(Guid tenant, string code)
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
        var slug = $"l-{Guid.NewGuid():N}"[..20];
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

    private async Task<IReadOnlyList<string>> LedgerPayloadsAsync(Guid tenant, string eventType)
    {
        await using var connection = new Npgsql.NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand($"SELECT payload::text FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = '{eventType}'", connection);
        var payloads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    [GeneratedRegex(@" re f\n")]
    private static partial Regex RectanglePattern();

    /// <summary>Just enough PDF reading to check the sheet: the cross-reference table points at every object, and the page contents inflate.</summary>
    private sealed record Pdf(IReadOnlyList<string> Pages)
    {
        public static Pdf Read(byte[] bytes)
        {
            var text = Encoding.Latin1.GetString(bytes);
            text.Should().StartWith("%PDF-1.4").And.EndWith("%%EOF\n");
            var xref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            text[xref..].Should().StartWith("xref\n");
            var offsets = Regex.Matches(text[xref..], @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            for (var i = 0; i < offsets.Count; i++)
            {
                text[offsets[i]..].Should().StartWith($"{i + 1} 0 obj\n", "the cross-reference table must point at each object");
            }

            var count = int.Parse(Regex.Match(text, @"/Type /Pages /Kids \[[^\]]*\] /Count (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var pages = new List<string>();
            foreach (Match stream in Regex.Matches(text, @"/Length (\d+) /Filter /FlateDecode >>\nstream\n"))
            {
                var length = int.Parse(stream.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                using var zlib = new ZLibStream(new MemoryStream(bytes, stream.Index + stream.Length, length), CompressionMode.Decompress);
                using var content = new MemoryStream();
                zlib.CopyTo(content);
                pages.Add(Encoding.Latin1.GetString(content.ToArray()));
            }

            pages.Should().HaveCount(count);
            return new Pdf(pages);
        }
    }
}
