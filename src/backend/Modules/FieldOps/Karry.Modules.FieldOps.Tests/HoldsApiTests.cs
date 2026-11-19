using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Karry.Modules.Assets.Contracts;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.FieldOps.Tests;

/// <summary>
/// Releasing a safety hold over HTTP (ADR-0035 §5, US-P2-FO-02 AC3): a manager of the hold's site, with a reason, once;
/// the decision is ledgered under their name; nobody else, and no other company, can release it.
/// </summary>
[Trait("Story", "US-P2-FO-02")]
public sealed class HoldsApiTests(FieldOpsHost host) : IClassFixture<FieldOpsHost>
{
    private const string Reason = "Brakes adjusted by the workshop, tested OK";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestCase", "TC-P2-FO-014")]
    public async Task A_manager_releases_a_hold_once_with_a_reason_and_the_decision_is_ledgered()
    {
        var tenant = await ProvisionAsync();
        var (hold, asset) = await HoldAsync(tenant, Guid.CreateVersion7());

        var released = await ReleaseAsync(tenant, hold, Reason);
        var again = await ReleaseAsync(tenant, hold, Reason);

        released.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(released);
        (body.GetProperty("assetId").GetGuid(), body.GetProperty("releasedBy").GetString(), body.GetProperty("reason").GetString()).Should().Be((asset, "admin-1", Reason));
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(again)).GetProperty("code").GetString().Should().Be("fieldops.hold_released");
        (await ScalarAsync<string>($"SELECT payload::jsonb->>'reason' FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'fieldops.safety_hold_released.v1'"))
            .Should().Be(Reason);
        await FluentActions.Awaiting(() => ScalarAsync<string>("UPDATE fieldops.safety_hold_releases SET reason = 'changed afterwards' RETURNING reason"))
            .Should().ThrowAsync<PostgresException>("a decision is append-only, even for the owner");
    }

    [Theory]
    [InlineData("too short")]
    [InlineData("")]
    public async Task A_release_needs_a_reason_of_ten_to_five_hundred_characters(string reason)
    {
        var tenant = await ProvisionAsync();
        var (hold, _) = await HoldAsync(tenant, Guid.CreateVersion7());

        var response = await ReleaseAsync(tenant, hold, reason);
        var tooLong = await ReleaseAsync(tenant, hold, new string('x', 501));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be("fieldops.release_reason_invalid");
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-FO-014")]
    public async Task Only_a_manager_of_the_holds_site_releases_it_and_another_company_never_sees_it()
    {
        var (tenant, other) = (await ProvisionAsync(), await ProvisionAsync());
        var site = Guid.CreateVersion7();
        var (hold, _) = await HoldAsync(tenant, site);
        host.SiteOf[FieldOpsHost.SiteManager] = Guid.CreateVersion7();

        var otherSiteManager = await ReleaseAsync(tenant, hold, Reason, TestTokens.ForTenant(tenant, FieldOpsHost.SiteManager));
        var operatorRelease = await ReleaseAsync(tenant, hold, Reason, TestTokens.ForTenant(tenant, FieldOpsHost.Operator));
        var otherCompany = await ReleaseAsync(other, hold, Reason);
        var unknown = await ReleaseAsync(tenant, Guid.CreateVersion7(), Reason);

        otherSiteManager.StatusCode.Should().Be(HttpStatusCode.NotFound, "outside the manager's sites the hold does not exist for them");
        operatorRelease.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        otherCompany.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        host.SiteOf[FieldOpsHost.SiteManager] = site;
        (await ReleaseAsync(tenant, hold, Reason, TestTokens.ForTenant(tenant, FieldOpsHost.SiteManager))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_list_shows_open_holds_of_the_callers_sites_and_released_ones_on_request()
    {
        var tenant = await ProvisionAsync();
        var (north, south) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var (held, heldAsset) = await HoldAsync(tenant, north);
        host.Assets.All[heldAsset] = new AssetSummary(heldAsset, "PE-07", "excavator", []);
        var (released, _) = await HoldAsync(tenant, north);
        var (elsewhere, _) = await HoldAsync(tenant, south);
        (await ReleaseAsync(tenant, released, Reason)).StatusCode.Should().Be(HttpStatusCode.OK);
        host.SiteOf[FieldOpsHost.SiteManager] = north;

        var open = await ListAsync(tenant, "");
        var done = await ListAsync(tenant, "?status=released");
        var all = await ListAsync(tenant, $"?status=all&siteId={north}");
        var manager = await ListAsync(tenant, "?status=all", TestTokens.ForTenant(tenant, FieldOpsHost.SiteManager));
        var wrong = await GetAsync(tenant, "?status=closed");

        open.Should().BeEquivalentTo([elsewhere, held]);
        done.Should().Equal(released);
        all.Should().BeEquivalentTo([held, released]);
        manager.Should().BeEquivalentTo([held, released], "a site manager sees the holds of their sites only");
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var row = (await Json(await GetAsync(tenant, "?status=released"))).GetProperty("items")[0];
        (row.GetProperty("items")[0].GetString(), row.GetProperty("release").GetProperty("reason").GetString()).Should().Be(("brakes", Reason));
        var first = (await Json(await GetAsync(tenant, $"?siteId={north}"))).GetProperty("items")[0];
        (first.GetProperty("assetCode").GetString(), first.GetProperty("release").ValueKind).Should().Be(("PE-07", JsonValueKind.Null));
    }

    private async Task<List<Guid>> ListAsync(Guid tenant, string query, string? token = null) =>
        [.. (await Json(await GetAsync(tenant, query, token))).GetProperty("items").EnumerateArray().Select(h => h.GetProperty("id").GetGuid())];

    private async Task<HttpResponseMessage> GetAsync(Guid tenant, string query, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/fieldops/holds{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForTenant(tenant, "admin-1"));
        return await host.Client.SendAsync(request, Ct);
    }

    /// <summary>A hold as a critical Not OK opens it (the handler is tested on its own): written by the owner.</summary>
    private async Task<(Guid Hold, Guid Asset)> HoldAsync(Guid tenant, Guid site)
    {
        var (hold, asset) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO fieldops.safety_holds (id, tenant_id, asset_id, site_id, submission_id, items, opened_at, opened_by) "
            + "VALUES (@id, @tenant, @asset, @site, @submission, ARRAY['brakes'], now(), @operator)", connection);
        command.Parameters.AddWithValue("id", hold);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("asset", asset);
        command.Parameters.AddWithValue("site", site);
        command.Parameters.AddWithValue("submission", Guid.CreateVersion7());
        command.Parameters.AddWithValue("operator", Guid.CreateVersion7());
        await command.ExecuteNonQueryAsync(Ct);
        return (hold, asset);
    }

    private async Task<HttpResponseMessage> ReleaseAsync(Guid tenant, Guid hold, string reason, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/fieldops/holds/{hold}/release") { Content = JsonContent.Create(new { reason }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.ForTenant(tenant, "admin-1"));
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> ProvisionAsync()
    {
        var slug = $"h-{Guid.NewGuid():N}"[..20];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenancy/tenants") { Content = JsonContent.Create(new { slug, name = $"Carrière {slug}" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForPlatformOperator());
        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
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
