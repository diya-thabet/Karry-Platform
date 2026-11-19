using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>
/// Pre-start checklist templates over HTTP (US-P2-FO-03, ADR-0035 §2): versions per asset type, newest first, frozen
/// once published, ledgered, checked, company-wide and never seen by another company.
/// </summary>
[Trait("Story", "US-P2-FO-03")]
public sealed class ChecklistsApiTests(FieldOpsHost host) : IClassFixture<FieldOpsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Item(string code, bool critical = false, bool photo = false, string fr = "Freins") =>
        new { code, texts = new Dictionary<string, string> { ["fr"] = fr, ["en"] = "Brakes" }, critical, photoOnNotOk = photo };

    [Fact]
    [Trait("TestCase", "TC-P2-FO-013")]
    public async Task Each_publish_is_the_next_frozen_version_and_the_list_shows_the_newest_per_asset_type()
    {
        var tenant = await ProvisionAsync();

        var first = await PublishAsync(tenant, "excavator", new { required = true, items = new[] { Item(" BRAKES ", critical: true), Item("lights") } });
        var second = await PublishAsync(tenant, "excavator", new { required = false, items = new[] { Item("brakes", critical: true), Item("horn", photo: true) } });
        await PublishAsync(tenant, "dump-truck", new { required = true, items = new[] { Item("tyres") } });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var v1 = await Json(first);
        (v1.GetProperty("version").GetInt32(), v1.GetProperty("required").GetBoolean()).Should().Be((1, true));
        var brakes = v1.GetProperty("items")[0];
        (brakes.GetProperty("code").GetString(), brakes.GetProperty("critical").GetBoolean(), brakes.GetProperty("photoOnNotOk").GetBoolean())
            .Should().Be(("brakes", true, true), "codes are normalised and a critical item always asks for a photo on Not OK");
        (await Json(second)).GetProperty("version").GetInt32().Should().Be(2);

        var versions = await Json(await GetAsync("/api/v1/fieldops/checklists/excavator/versions", Admin(tenant)));
        versions.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()).Should().Equal(2, 1);
        versions.GetProperty("items")[1].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString())
            .Should().Equal(["brakes", "lights"], "version 1 stays as it was published");
        var latest = await Json(await GetAsync("/api/v1/fieldops/checklists", Admin(tenant)));
        latest.GetProperty("items").EnumerateArray().Select(c => $"{c.GetProperty("assetType").GetString()} v{c.GetProperty("version").GetInt32()}")
            .Should().Equal("dump-truck v1", "excavator v2");
    }

    [Theory]
    [InlineData("Excavator", "fieldops.asset_type_invalid")]
    [InlineData("excavator", "fieldops.checklist_items_invalid", true)]
    public async Task A_wrong_asset_type_or_no_item_is_refused(string assetType, string code, bool noItems = false)
    {
        var tenant = await ProvisionAsync();

        var response = await PublishAsync(tenant, assetType, new { required = true, items = noItems ? Array.Empty<object>() : new[] { Item("brakes") } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task Items_need_unique_codes_and_a_text_in_a_supported_language()
    {
        var tenant = await ProvisionAsync();

        var duplicate = await PublishAsync(tenant, "excavator", new { required = true, items = new[] { Item("brakes"), Item("Brakes") } });
        var unknownLanguage = await PublishAsync(tenant, "excavator", new { required = true, items = new[] { new { code = "brakes", texts = new Dictionary<string, string> { ["de"] = "Bremsen" }, critical = false, photoOnNotOk = false } } });
        var tooLong = await PublishAsync(tenant, "excavator", new { required = true, items = new[] { Item("brakes", fr: new string('x', 201)) } });

        (await Json(duplicate)).GetProperty("code").GetString().Should().Be("fieldops.checklist_item_code_duplicate");
        (await Json(unknownLanguage)).GetProperty("code").GetString().Should().Be("fieldops.checklist_item_text_invalid");
        (await Json(tooLong)).GetProperty("code").GetString().Should().Be("fieldops.checklist_item_text_invalid");
        (await GetAsync("/api/v1/fieldops/checklists/excavator/versions", Admin(tenant))).StatusCode.Should().Be(HttpStatusCode.NotFound, "nothing was published");
    }

    [Fact]
    public async Task Only_company_wide_managers_publish_and_a_field_role_does_not_even_read()
    {
        var tenant = await ProvisionAsync();
        host.SiteOf[FieldOpsHost.SiteManager] = Guid.CreateVersion7();

        var siteManager = await PublishAsync(tenant, "excavator", new { required = true, items = new[] { Item("brakes") } }, TestTokens.ForTenant(tenant, FieldOpsHost.SiteManager));
        var reading = await GetAsync("/api/v1/fieldops/checklists", TestTokens.ForTenant(tenant, FieldOpsHost.Operator));

        siteManager.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a checklist is company-wide: a site assignment does not grant it");
        reading.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_published_version_is_ledgered_never_changed_and_never_seen_by_another_company()
    {
        var (tenant, other) = (await ProvisionAsync(), await ProvisionAsync());
        (await PublishAsync(tenant, "excavator", new { required = true, items = new[] { Item("brakes", critical: true) } })).StatusCode.Should().Be(HttpStatusCode.Created);

        var otherList = await Json(await GetAsync("/api/v1/fieldops/checklists", Admin(other)));
        otherList.GetProperty("items").GetArrayLength().Should().Be(0);
        (await ScalarAsync<long>($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'fieldops.checklist_published.v1'")).Should().Be(1);
        await FluentActions.Awaiting(() => ScalarAsync<long>("UPDATE fieldops.checklist_templates SET required = false RETURNING 1"))
            .Should().ThrowAsync<PostgresException>("a published version is append-only, even for the owner");
    }

    private static string Admin(Guid tenant) => TestTokens.ForTenant(tenant, "admin-1");

    private async Task<HttpResponseMessage> PublishAsync(Guid tenant, string assetType, object body, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/fieldops/checklists/{assetType}/versions") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Admin(tenant));
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"c-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
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

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
