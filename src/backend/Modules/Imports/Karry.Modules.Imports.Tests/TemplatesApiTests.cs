using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;

namespace Karry.Modules.Imports.Tests;

/// <summary>Templates over HTTP (US-P1-ORG-03 AC1): per kind and language, for those who may import that kind only.</summary>
[Trait("Story", "US-P1-ORG-03")]
public sealed class TemplatesApiTests(ImportsHost host) : IClassFixture<ImportsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_template_is_an_xlsx_in_the_chosen_language_with_the_kinds_columns_and_lists()
    {
        var tenant = await ProvisionAsync();

        using var english = await GetAsync("/api/v1/imports/templates/probes?language=en", Admin(tenant));
        english.StatusCode.Should().Be(HttpStatusCode.OK);
        english.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        english.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("karry-probes-en.xlsx");
        using (var workbook = new XLWorkbook(await english.Content.ReadAsStreamAsync(Ct)))
        {
            workbook.Worksheet(1).Row(1).CellsUsed().Select(c => c.GetText()).Should().Equal("Code *", "Name *", "Site");
            workbook.Worksheet("Lists").Cell("A2").GetText().Should().Be("CAR-NORD");
        }

        using var french = await GetAsync("/api/v1/imports/templates/probes", Admin(tenant));
        using var workbookFr = new XLWorkbook(await french.Content.ReadAsStreamAsync(Ct));
        workbookFr.Worksheet(1).Name.Should().Be("Données", "French by default");
        workbookFr.Worksheet(1).Cell("B1").GetText().Should().Be("Nom *");
    }

    [Fact]
    public async Task An_unknown_kind_or_language_is_refused_and_so_is_a_caller_who_may_not_import_or_change_the_data()
    {
        var tenant = await ProvisionAsync();

        (await Problem(await GetAsync("/api/v1/imports/templates/rockets", Admin(tenant)))).Should().Be((HttpStatusCode.NotFound, "imports.kind_unknown"));
        (await Problem(await GetAsync("/api/v1/imports/templates/probes?language=de", Admin(tenant)))).Should().Be((HttpStatusCode.BadRequest, "imports.language_unsupported"));
        (await Problem(await GetAsync("/api/v1/imports/templates/locked", Admin(tenant)))).Should().Be((HttpStatusCode.Forbidden, "imports.kind_forbidden"));
        (await GetAsync("/api/v1/imports/templates/probes", TestTokens.ForTenant(tenant, ImportsHost.Operator))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static string Admin(Guid tenant) => TestTokens.ForTenant(tenant, "admin-1");

    private static async Task<(HttpStatusCode, string?)> Problem(HttpResponseMessage response)
    {
        using (response)
        {
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"i-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }
}
