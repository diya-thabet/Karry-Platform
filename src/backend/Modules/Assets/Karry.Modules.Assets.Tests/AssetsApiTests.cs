using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Tenancy;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Karry.Modules.Assets.Tests;

/// <summary>
/// The register over HTTP (US-P1-AST-01, US-P1-AST-02): assets registered, changed, moved and disposed, each change in
/// the company's ledger; an append-only site history; search and filters; a site role reaches only its sites; the asset
/// limit follows the soft-limit rules.
/// </summary>
[Trait("Story", "US-P1-AST-02")]
public sealed class AssetsApiTests(AssetsHost host) : IClassFixture<AssetsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Input(string code, Guid siteId, string type = "excavator", string? plate = null, string? make = "Caterpillar", string? model = "336") => new
    {
        code,
        name = $"Machine {code}",
        type,
        make,
        model,
        serialNumber = " cat0336-xe12 ",
        plate,
        year = 2021,
        acquiredOn = "2021-03-15",
        siteId,
    };

    [Fact]
    public async Task The_type_catalogue_lists_the_seeded_types_with_their_default_meters()
    {
        var tenant = await ProvisionAsync();

        var types = await Items(await SendAsync(HttpMethod.Get, "/api/v1/assets/types", Admin(tenant)));

        types.Select(t => t.GetProperty("code").GetString()).Should().HaveCount(17).And.Contain(["excavator", "crusher-vsi", "fuel-bowser", "other"]);
        var truck = types.Single(t => t.GetProperty("code").GetString() == "haul-truck");
        truck.GetProperty("defaultMeters").EnumerateArray().Select(m => m.GetString()).Should().Equal("hours", "km");
        truck.GetProperty("components").EnumerateArray().Select(c => c.GetString()).Should().Contain("tyres");
        types.Single(t => t.GetProperty("code").GetString() == "other").GetProperty("defaultMeters").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_administrator_registers_an_asset_normalized_on_its_site_and_in_the_ledger_and_its_code_stays_taken()
    {
        var tenant = await ProvisionAsync();
        var site = await SiteAsync(tenant, "nord");

        var created = await SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), Input("pel-01", site, plate: " 123  tu 4567 "));

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var asset = await Json(created);
        asset.GetProperty("code").GetString().Should().Be("PEL-01");
        asset.GetProperty("plate").GetString().Should().Be("123 TU 4567");
        asset.GetProperty("serialNumber").GetString().Should().Be("CAT0336-XE12");
        asset.GetProperty("status").GetString().Should().Be("active");
        asset.GetProperty("siteId").GetGuid().Should().Be(site);
        asset.GetProperty("sites").EnumerateArray().Single().GetProperty("siteId").GetGuid().Should().Be(site);
        (await LedgerCountAsync(tenant, "assets.asset_registered.v1")).Should().Be(1);

        var again = await SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), Input("PEL-01", site));
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(again)).GetProperty("code").GetString().Should().Be("assets.asset_code_taken");
    }

    [Theory]
    [InlineData("type", "crane", "assets.type_invalid")]
    [InlineData("year", 1949, "assets.year_invalid")]
    [InlineData("acquiredOn", "2999-01-01", "assets.acquired_on_invalid")]
    [InlineData("code", "-x", "assets.code_invalid")]
    public async Task Invalid_details_are_refused_with_their_error(string field, object value, string error)
    {
        var tenant = await ProvisionAsync();
        var site = await SiteAsync(tenant, "nord");
        var input = JsonSerializer.SerializeToNode(Input("PEL-01", site), JsonSerializerOptions.Web)!.AsObject();
        input[field] = JsonSerializer.SerializeToNode(value);

        var response = await SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), input);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be(error);
        (await LedgerCountAsync(tenant, "assets.asset_registered.v1")).Should().Be(0);
    }

    [Fact]
    public async Task An_asset_on_an_archived_or_unknown_site_is_refused()
    {
        var tenant = await ProvisionAsync();
        var site = await SiteAsync(tenant, "old");
        (await SendAsync(HttpMethod.Post, $"/api/v1/organization/sites/{site}/archive", Admin(tenant))).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var siteId in new[] { site, Guid.NewGuid() })
        {
            var response = await SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), Input("PEL-01", siteId));
            (await Json(response)).GetProperty("code").GetString().Should().Be("assets.site_invalid");
        }
    }

    [Fact]
    public async Task A_change_is_ledgered_once_and_a_disposed_asset_keeps_its_details_until_it_is_back()
    {
        var tenant = await ProvisionAsync();
        var site = await SiteAsync(tenant, "nord");
        var id = await RegisterAsync(tenant, "PEL-01", site);
        var change = new { name = "Pelle 336", type = "excavator", make = "Caterpillar", model = "336 GC", serialNumber = "CAT0336-XE12", plate = (string?)null, year = 2021, acquiredOn = "2021-03-15" };

        var changed = await SendAsync(HttpMethod.Put, $"/api/v1/assets/{id}", Admin(tenant), change);
        (await Json(changed)).GetProperty("model").GetString().Should().Be("336 GC");
        await SendAsync(HttpMethod.Put, $"/api/v1/assets/{id}", Admin(tenant), change);
        (await LedgerCountAsync(tenant, "assets.asset_changed.v1")).Should().Be(1, "the same details again change nothing");

        var disposed = await SendAsync(HttpMethod.Post, $"/api/v1/assets/{id}/status", Admin(tenant), new { status = "disposed" });
        (await Json(disposed)).GetProperty("disposedAt").ValueKind.Should().Be(JsonValueKind.String);
        var refused = await SendAsync(HttpMethod.Put, $"/api/v1/assets/{id}", Admin(tenant), change);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(refused)).GetProperty("code").GetString().Should().Be("assets.asset_disposed");

        var back = await SendAsync(HttpMethod.Post, $"/api/v1/assets/{id}/status", Admin(tenant), new { status = "idle" });
        (await Json(back)).GetProperty("disposedAt").ValueKind.Should().Be(JsonValueKind.Null);
        (await LedgerCountAsync(tenant, "assets.asset_status_changed.v1")).Should().Be(2);
        (await SendAsync(HttpMethod.Post, $"/api/v1/assets/{id}/status", Admin(tenant), new { status = "broken" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_move_adds_an_arrival_to_the_site_history_which_the_database_never_lets_change()
    {
        var tenant = await ProvisionAsync();
        var nord = await SiteAsync(tenant, "nord");
        var sud = await SiteAsync(tenant, "sud");
        var id = await RegisterAsync(tenant, "PEL-01", nord);

        var moved = await Json(await SendAsync(HttpMethod.Post, $"/api/v1/assets/{id}/move", Admin(tenant), new { siteId = sud }));
        await SendAsync(HttpMethod.Post, $"/api/v1/assets/{id}/move", Admin(tenant), new { siteId = sud });

        moved.GetProperty("siteId").GetGuid().Should().Be(sud);
        moved.GetProperty("sites").EnumerateArray().Select(s => s.GetProperty("siteId").GetGuid()).Should().Equal(nord, sud);
        (await LedgerCountAsync(tenant, "assets.asset_moved.v1")).Should().Be(1, "moving to the site it is on changes nothing");

        // TC-P1-AST-002 (site history): the application role cannot rewrite where an asset was.
        foreach (var sql in new[]
        {
            $"UPDATE assets.site_assignments SET site_id = '{nord}' WHERE asset_id = '{id}'",
            $"DELETE FROM assets.site_assignments WHERE asset_id = '{id}'",
        })
        {
            var act = () => InTenantAsync(host.Database.App, tenant, sql);
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        // Even the owner (migrations) hits the trigger.
        var owner = () => InTenantAsync(host.Database.Owner, tenant, $"DELETE FROM assets.site_assignments WHERE asset_id = '{id}'");
        (await owner.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task Search_matches_fragments_and_filters_by_site_type_and_status_page_by_page()
    {
        var tenant = await ProvisionAsync();
        var nord = await SiteAsync(tenant, "nord");
        var sud = await SiteAsync(tenant, "sud");
        await RegisterAsync(tenant, "PEL-01", nord, plate: "123 TU 4567");
        await RegisterAsync(tenant, "PEL-02", sud, make: "Volvo", model: "EC480");
        await RegisterAsync(tenant, "DUM-01", nord, type: "haul-truck", make: "Komatsu", model: "HD785");
        var disposed = await RegisterAsync(tenant, "DUM-02", nord, type: "haul-truck");
        await SendAsync(HttpMethod.Post, $"/api/v1/assets/{disposed}/status", Admin(tenant), new { status = "disposed" });

        (await Codes(tenant, "")).Should().Equal("DUM-01", "PEL-01", "PEL-02");
        (await Codes(tenant, "?q=tu 45")).Should().Equal("PEL-01");
        (await Codes(tenant, "?q=ec4")).Should().Equal("PEL-02");
        (await Codes(tenant, "?q=komatsu")).Should().Equal("DUM-01");
        (await Codes(tenant, "?q=pel-")).Should().Equal("PEL-01", "PEL-02");
        (await Codes(tenant, "?q=%25")).Should().BeEmpty("% is a character to find, not a wildcard");
        (await Codes(tenant, $"?siteId={nord}")).Should().Equal("DUM-01", "PEL-01");
        (await Codes(tenant, "?type=haul-truck")).Should().Equal("DUM-01");
        (await Codes(tenant, "?status=disposed")).Should().Equal("DUM-02");

        var first = await Json(await SendAsync(HttpMethod.Get, "/api/v1/assets?limit=2", Admin(tenant)));
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await Json(await SendAsync(HttpMethod.Get, $"/api/v1/assets?limit=2&cursor={cursor}", Admin(tenant)));
        second.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("code").GetString()).Should().Equal("PEL-02");
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        foreach (var (query, error) in new[] { ("?limit=201", "assets.limit_invalid"), ("?type=crane", "assets.type_invalid"), ("?status=lost", "assets.status_invalid") })
        {
            (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/assets{query}", Admin(tenant)))).GetProperty("code").GetString().Should().Be(error);
        }
    }

    [Fact]
    public async Task A_site_manager_reaches_only_the_assets_on_their_site_and_an_operator_registers_none()
    {
        var tenant = await ProvisionAsync();
        var nord = await SiteAsync(tenant, "nord");
        var sud = await SiteAsync(tenant, "sud");
        var onNord = await RegisterAsync(tenant, "PEL-01", nord);
        var onSud = await RegisterAsync(tenant, "PEL-02", sud);
        host.SiteOf[AssetsHost.SiteManager] = nord;
        host.SiteOf[AssetsHost.Operator] = nord;
        var manager = TestTokens.ForTenant(tenant, AssetsHost.SiteManager);

        (await Codes(tenant, "", manager)).Should().Equal("PEL-01");
        (await Codes(tenant, "", TestTokens.ForTenant(tenant, AssetsHost.Executive))).Should().Equal("PEL-01", "PEL-02");
        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/{onSud}", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, $"/api/v1/assets/{onSud}/status", manager, new { status = "idle" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(await SendAsync(HttpMethod.Post, "/api/v1/assets", manager, Input("PEL-03", sud)))).GetProperty("code").GetString().Should().Be("assets.site_invalid");
        (await Json(await SendAsync(HttpMethod.Post, $"/api/v1/assets/{onNord}/move", manager, new { siteId = sud }))).GetProperty("code").GetString()
            .Should().Be("assets.site_invalid", "moving needs the destination site too");
        (await SendAsync(HttpMethod.Post, "/api/v1/assets", manager, Input("PEL-03", nord))).StatusCode.Should().Be(HttpStatusCode.Created);

        var @operator = TestTokens.ForTenant(tenant, AssetsHost.Operator);
        (await Codes(tenant, "", @operator)).Should().Equal("PEL-01", "PEL-03");
        (await SendAsync(HttpMethod.Post, "/api/v1/assets", @operator, Input("PEL-04", nord))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/assets/{onNord}/status", @operator, new { status = "idle" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_asset_limit_counts_assets_not_disposed_with_the_soft_limit_grace()
    {
        var tenant = await ProvisionAsync();
        var site = await SiteAsync(tenant, "nord");
        host.Limits.ByTenant[tenant] = 1;

        await RegisterAsync(tenant, "PEL-01", site);
        var second = await RegisterAsync(tenant, "PEL-02", site);
        var third = await SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), Input("PEL-03", site));
        third.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a limit of 1 lets one more in (grace), not two");
        (await Json(third)).GetProperty("code").GetString().Should().Be("billing.limit_reached");

        await SendAsync(HttpMethod.Post, $"/api/v1/assets/{second}/status", Admin(tenant), new { status = "disposed" });
        (await UsageAsync(tenant)).Should().Be(1);
        var replacement = await RegisterAsync(tenant, "PEL-03", site);
        (await SendAsync(HttpMethod.Post, $"/api/v1/assets/{second}/status", Admin(tenant), new { status = "active" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "bringing a disposed asset back counts it again");
        await SendAsync(HttpMethod.Post, $"/api/v1/assets/{replacement}/status", Admin(tenant), new { status = "in-repair" });
        (await UsageAsync(tenant)).Should().Be(2, "an asset in repair still counts");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task A_change_made_from_an_old_version_is_refused_and_the_newer_change_is_kept()
    {
        // Hardening plan H-C1: two people opened the same machine; the second to save must not overwrite the first.
        var tenant = await ProvisionAsync();
        var id = await RegisterAsync(tenant, "PEL-01", await SiteAsync(tenant, "nord"));
        var opened = await SendAsync(HttpMethod.Get, $"/api/v1/assets/{id}", Admin(tenant));
        var version = opened.Headers.ETag!.Tag;
        (await Json(opened)).GetProperty("version").GetString().Should().Be(version, "body and header carry the same version");

        var first = await SendAsync(HttpMethod.Put, $"/api/v1/assets/{id}", Admin(tenant), Change("336 GC"), ifMatch: version);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag!.Tag.Should().NotBe(version, "every saved change makes a new version");

        var second = await SendAsync(HttpMethod.Put, $"/api/v1/assets/{id}", Admin(tenant), Change("336 NEXT"), ifMatch: version);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(second)).GetProperty("code").GetString().Should().Be("request.version_conflict");

        (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{id}", Admin(tenant)))).GetProperty("model").GetString().Should().Be("336 GC");
        (await LedgerCountAsync(tenant, "assets.asset_changed.v1")).Should().Be(1, "the refused change left no trace");

        var retried = await SendAsync(HttpMethod.Put, $"/api/v1/assets/{id}", Admin(tenant), Change("336 NEXT"), ifMatch: first.Headers.ETag.Tag);
        retried.StatusCode.Should().Be(HttpStatusCode.OK, "from the current version, the same change goes through");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task Of_many_saves_from_one_version_at_the_same_moment_exactly_one_is_applied()
    {
        // The race between loading and saving: the check in the UPDATE (xmin), not only the comparison in the service.
        var tenant = await ProvisionAsync();
        var id = await RegisterAsync(tenant, "PEL-01", await SiteAsync(tenant, "nord"));
        var version = (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{id}", Admin(tenant)))).GetProperty("version").GetString()!;

        var saves = await Task.WhenAll(Enumerable.Range(1, 8).Select(n =>
            SendAsync(HttpMethod.Put, $"/api/v1/assets/{id}", Admin(tenant), Change($"336-{n}"), ifMatch: version)));

        saves.Count(s => s.StatusCode == HttpStatusCode.OK).Should().Be(1);
        saves.Count(s => s.StatusCode == HttpStatusCode.Conflict).Should().Be(7, "every other save is a version conflict, never a 500");
        (await LedgerCountAsync(tenant, "assets.asset_changed.v1")).Should().Be(1);
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task Status_changes_and_moves_at_the_same_moment_answer_200_only_for_what_was_saved()
    {
        // No If-Match on these, but two loads of the same version still race: the loser must hear 409, not 200 for a
        // change that was never saved (the service used to ignore the failed save). Every request below changes the
        // asset it loaded, so each 200 is one saved version, and each saved version is one ledger entry.
        var tenant = await ProvisionAsync();
        var sud = await SiteAsync(tenant, "sud");
        var id = await RegisterAsync(tenant, "PEL-01", await SiteAsync(tenant, "nord"));
        var registered = (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{id}", Admin(tenant)))).GetProperty("version").GetString();

        var calls = await Task.WhenAll(Enumerable.Range(0, 8).Select(n => n switch
        {
            0 => SendAsync(HttpMethod.Post, $"/api/v1/assets/{id}/move", Admin(tenant), new { siteId = sud }),
            _ => SendAsync(HttpMethod.Post, $"/api/v1/assets/{id}/status", Admin(tenant), new { status = n % 2 == 0 ? "idle" : "in-repair" }),
        }));

        calls.Should().OnlyContain(c => c.StatusCode == HttpStatusCode.OK || c.StatusCode == HttpStatusCode.Conflict);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls.Where(c => c.StatusCode == HttpStatusCode.OK))
        {
            versions.Add((await Json(call)).GetProperty("version").GetString()!);
        }

        versions.Should().NotContain(registered, "a 200 always reports a saved change");
        var ledgered = await LedgerCountAsync(tenant, "assets.asset_status_changed.v1") + await LedgerCountAsync(tenant, "assets.asset_moved.v1");
        ledgered.Should().Be(versions.Count, "every version answered was saved and ledgered, and nothing else was");
    }

    [Theory]
    [Trait("Story", "P1.18a")]
    [InlineData("4711")]
    [InlineData("\"abc\"")]
    [InlineData("\"1\", \"2\"")]
    public async Task A_malformed_if_match_is_refused_and_nothing_changes(string ifMatch)
    {
        var tenant = await ProvisionAsync();
        var id = await RegisterAsync(tenant, "PEL-01", await SiteAsync(tenant, "nord"));

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/assets/{id}", Admin(tenant), Change("336 GC"), ifMatch: ifMatch);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be("request.if_match_invalid");
        (await Json(await SendAsync(HttpMethod.Get, $"/api/v1/assets/{id}", Admin(tenant)))).GetProperty("model").GetString().Should().Be("336");
    }

    private static object Change(string model) =>
        new { name = "Pelle 336", type = "excavator", make = "Caterpillar", model, serialNumber = (string?)null, plate = (string?)null, year = 2021, acquiredOn = "2021-03-15" };

    private static string Admin(Guid tenant) => TestTokens.ForTenant(tenant, "admin-1");

    private async Task<IReadOnlyList<string?>> Codes(Guid tenant, string query, string? token = null) =>
        [.. (await Items(await SendAsync(HttpMethod.Get, $"/api/v1/assets{query}", token ?? Admin(tenant)))).Select(a => a.GetProperty("code").GetString())];

    private async Task<Guid> RegisterAsync(Guid tenant, string code, Guid site, string type = "excavator", string? plate = null, string? make = "Caterpillar", string? model = "336")
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), Input(code, site, type, plate, make, model));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

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
        var slug = $"a-{Guid.NewGuid():N}"[..20];
        var response = await SendAsync(HttpMethod.Post, "/api/v1/tenancy/tenants", TestTokens.ForPlatformOperator(), new { slug, name = $"Carrière {slug}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<long> UsageAsync(Guid tenant)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(new TenantId(tenant));
        return await scope.ServiceProvider.GetServices<IUsageSource>().Single(s => s.Metric == Metrics.Assets).CountAsync(Ct);
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

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

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

    /// <summary>Runs <paramref name="sql"/> on <paramref name="connectionString"/> inside the tenant's RLS context.</summary>
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
