using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;
using Npgsql;

namespace Karry.Modules.Assets.Tests;

/// <summary>
/// The asset photo (US-P1-AST-02 AC1, ADR-0026): an image in object storage under the company's prefix, its type read
/// from the content, served only to callers who see the asset, each change in the company's ledger.
/// </summary>
[Trait("Story", "US-P1-AST-02")]
public sealed class PhotosApiTests(AssetsHost host) : IClassFixture<AssetsHost>
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 1, 2, 3];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_photo_is_stored_under_the_company_served_as_an_image_and_replaced_without_leaving_the_old_one()
    {
        var tenant = await ProvisionAsync();
        var asset = await RegisterAsync(tenant, await SiteAsync(tenant, "nord"));

        var set = await Json(await PutPhotoAsync(tenant, asset, Jpeg, "image/png"));
        var sha = Convert.ToHexStringLower(SHA256.HashData(Jpeg));
        set.GetProperty("photo").GetProperty("sha256").GetString().Should().Be(sha);
        set.GetProperty("photo").GetProperty("contentType").GetString().Should().Be("image/jpeg", "the type comes from the content, not from the request");
        host.Files.Objects.Keys.Should().Contain($"tenants/{tenant}/assets/{asset}/photo-{sha}");

        var served = await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/photo", Admin(tenant));
        served.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await served.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(Jpeg);
        served.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        served.Headers.ETag!.Tag.Should().Be($"\"{sha}\"");

        await PutPhotoAsync(tenant, asset, Png);
        host.Files.Objects.Keys.Where(k => k.Contains(asset.ToString(), StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().EndWith(Convert.ToHexStringLower(SHA256.HashData(Png)), "the replaced photo is removed");
        await PutPhotoAsync(tenant, asset, Png);
        (await LedgerCountAsync(tenant)).Should().Be(2, "the same photo again changes nothing");

        (await Json(await SendAsync(HttpMethod.Delete, $"/api/v1/assets/{asset}/photo", Admin(tenant)))).GetProperty("photo").ValueKind.Should().Be(JsonValueKind.Null);
        host.Files.Objects.Keys.Should().NotContain(k => k.Contains(asset.ToString(), StringComparison.Ordinal));
        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/photo", Admin(tenant))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LedgerCountAsync(tenant)).Should().Be(3);
    }

    [Fact]
    public async Task Anything_but_a_jpeg_png_or_webp_up_to_5_mb_is_refused_and_nothing_is_stored()
    {
        var tenant = await ProvisionAsync();
        var asset = await RegisterAsync(tenant, await SiteAsync(tenant, "nord"));
        var tooLarge = new byte[(5 * 1024 * 1024) + 1];
        Jpeg.CopyTo(tooLarge, 0);

        (await Error(await PutPhotoAsync(tenant, asset, "<svg onload=alert(1)>"u8.ToArray(), "image/png"))).Should().Be("assets.photo_type_unsupported");
        (await Error(await PutPhotoAsync(tenant, asset, tooLarge))).Should().Be("assets.photo_too_large");
        (await Error(await PutPhotoAsync(tenant, asset, []))).Should().Be("assets.photo_type_unsupported");
        host.Files.Objects.Keys.Should().NotContain(k => k.Contains(asset.ToString(), StringComparison.Ordinal));
        (await LedgerCountAsync(tenant)).Should().Be(0);
    }

    [Fact]
    public async Task A_photo_is_seen_only_by_those_who_see_the_asset_and_changed_by_those_who_manage_it()
    {
        var tenant = await ProvisionAsync();
        var nord = await SiteAsync(tenant, "nord");
        var onSud = await RegisterAsync(tenant, await SiteAsync(tenant, "sud"));
        await PutPhotoAsync(tenant, onSud, Jpeg);
        host.SiteOf[AssetsHost.Operator] = nord;

        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/{onSud}/photo", TestTokens.ForTenant(tenant, AssetsHost.Operator))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/{onSud}/photo", TestTokens.ForTenant(await ProvisionAsync(), "admin-1"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PutPhotoAsync(tenant, onSud, Png, token: TestTokens.ForTenant(tenant, AssetsHost.Operator))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task While_storage_is_down_photos_answer_503_with_retry_after_and_the_rest_keeps_working()
    {
        // Hardening plan H-R4. Removing a photo needs no storage until after it is saved (next test).
        var tenant = await ProvisionAsync();
        var asset = await RegisterAsync(tenant, await SiteAsync(tenant, "nord"));
        await PutPhotoAsync(tenant, asset, Jpeg);
        var ledger = await LedgerCountAsync(tenant);

        host.Files.Down = true;
        try
        {
            foreach (var response in new[]
            {
                await PutPhotoAsync(tenant, asset, Png),
                await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/photo", Admin(tenant)),
            })
            {
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                (await Error(response)).Should().Be("server.unavailable");
                response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
            }

            (await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}", Admin(tenant))).StatusCode.Should().Be(HttpStatusCode.OK, "only photos need the storage");
            (await SiteAsync(tenant, "sud")).Should().NotBeEmpty();
        }
        finally
        {
            host.Files.Down = false;
        }

        (await LedgerCountAsync(tenant)).Should().Be(ledger, "no photo change was recorded");
        var kept = await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/photo", Admin(tenant));
        (await kept.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(Jpeg, "the photo from before the outage is untouched");
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task A_change_saved_before_the_storage_stops_is_not_reported_as_failed()
    {
        // The new photo and the asset are saved; only removing the replaced object fails.
        var tenant = await ProvisionAsync();
        var asset = await RegisterAsync(tenant, await SiteAsync(tenant, "nord"));
        await PutPhotoAsync(tenant, asset, Jpeg);

        host.Files.DeletesDown = true;
        try
        {
            (await PutPhotoAsync(tenant, asset, Png)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await SendAsync(HttpMethod.Delete, $"/api/v1/assets/{asset}/photo", Admin(tenant))).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            host.Files.DeletesDown = false;
        }

        (await SendAsync(HttpMethod.Get, $"/api/v1/assets/{asset}/photo", Admin(tenant))).StatusCode.Should().Be(HttpStatusCode.NotFound, "the removal was saved");
    }

    private static string Admin(Guid tenant) => TestTokens.ForTenant(tenant, "admin-1");

    private async Task<HttpResponseMessage> PutPhotoAsync(Guid tenant, Guid asset, byte[] content, string contentType = "image/jpeg", string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/assets/{asset}/photo") { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Admin(tenant));
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<Guid> RegisterAsync(Guid tenant, Guid site)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/assets", Admin(tenant), new { code = "PEL-01", name = "Pelle", type = "excavator", siteId = site });
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
        var slug = $"p-{Guid.NewGuid():N}"[..20];
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

    private async Task<long> LedgerCountAsync(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM audit.ledger_entries WHERE tenant_id = '{tenant}' AND event_type = 'assets.asset_photo_changed.v1'", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
