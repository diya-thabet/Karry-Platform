using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Karry.Testing;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// An entry's photos for the controller console (ADR-0038 §2): the API reads the photo from the evidence bucket under
/// the key of the tablet that sent it and streams it to a person who sees the entry, only for a photo the entry names;
/// the entry's detail carries the segment before it on the same meter.
/// </summary>
[Trait("Story", "US-P2-VER-07")]
public sealed class EntryPhotosTests(BlobSyncHost host) : IClassFixture<BlobSyncHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_person_who_sees_the_entry_gets_the_photos_it_names_and_nobody_gets_anything_else()
    {
        var company = await host.CompanyAsync("photos");
        var other = await host.CompanyAsync("photos2");
        await SignedEntries.SeedShiftsAsync(host, company);
        using var tablet = await host.TabletAsync(company);
        var (startPhoto, endPhoto, stranger) = (Photo(), Photo(), Photo());
        foreach (var photo in new[] { startPhoto, endPhoto, stranger })
        {
            await UploadAsync(tablet, photo);
        }

        var (earlier, later) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var at = host.Clock.GetUtcNow().AddHours(2);
        var upload = await tablet.SendAsync(HttpMethod.Post, "/api/v1/sync/batches", new
        {
            commands = new[]
            {
                Start(tablet, 1, company, earlier, at, "10.0", startPhoto),
                End(tablet, 2, company, earlier, at.AddHours(4), "14.0", endPhoto),
                Start(tablet, 3, company, later, at.AddHours(5), "14.0", Photo()),
            },
        });
        upload.StatusCode.Should().Be(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync(Ct));

        using var start = await GetAsync($"/api/v1/fieldops/entries/{earlier}/photos/{Hex(startPhoto)}", host.Admin(company));
        using var end = await GetAsync($"/api/v1/fieldops/entries/{earlier}/photos/sha256:{Hex(endPhoto)}", host.Admin(company));
        using var notNamed = await GetAsync($"/api/v1/fieldops/entries/{earlier}/photos/{Hex(stranger)}", host.Admin(company));
        using var malformed = await GetAsync($"/api/v1/fieldops/entries/{earlier}/photos/{Hex(startPhoto).ToUpperInvariant()}", host.Admin(company));
        using var elsewhere = await GetAsync($"/api/v1/fieldops/entries/{earlier}/photos/{Hex(startPhoto)}", host.Admin(other));
        using var detail = await GetAsync($"/api/v1/fieldops/entries/{later}", host.Admin(company));
        using var first = await GetAsync($"/api/v1/fieldops/entries/{earlier}", host.Admin(company));

        start.StatusCode.Should().Be(HttpStatusCode.OK);
        (await start.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(startPhoto);
        start.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        var cache = start.Headers.CacheControl!;
        (cache.Private, cache.MaxAge).Should().Be((true, TimeSpan.FromDays(1)));
        cache.Extensions.Select(e => e.Name).Should().Equal("immutable");
        start.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        (await end.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(endPhoto, "the end photo, with or without its prefix");
        notNamed.StatusCode.Should().Be(HttpStatusCode.NotFound, "a photo in the bucket that this entry does not name");
        malformed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        elsewhere.StatusCode.Should().Be(HttpStatusCode.NotFound, "another company never reaches the entry");
        var previous = (await detail.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("previousOnMeter");
        (previous.GetProperty("id").GetGuid(), previous.GetProperty("endPhoto").GetString()).Should().Be((earlier, "sha256:" + Hex(endPhoto)));
        (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("previousOnMeter").ValueKind.Should().Be(JsonValueKind.Null, "the meter's first entry");
    }

    private static JsonObject Start(TestTablet tablet, int sequence, Company company, Guid root, DateTimeOffset at, string value, byte[] photo) =>
        SignedEntries.Command(tablet, sequence, "fieldops.shift_entry.start.v1", 1, company.Operator, new JsonObject
        {
            ["rootId"] = root.ToString(),
            ["version"] = 1,
            ["assetId"] = company.Excavator.ToString(),
            ["meterId"] = company.Meter.ToString(),
            ["startedAt"] = SignedEntries.Instant(at),
            ["startValue"] = value,
            ["fuelStartPercent"] = 80,
            ["startPhoto"] = "sha256:" + Hex(photo),
        });

    private static JsonObject End(TestTablet tablet, int sequence, Company company, Guid root, DateTimeOffset at, string value, byte[] photo) =>
        SignedEntries.Command(tablet, sequence, "fieldops.shift_entry.end.v1", 2, company.Operator, new JsonObject
        {
            ["rootId"] = root.ToString(),
            ["version"] = 2,
            ["endedAt"] = SignedEntries.Instant(at),
            ["endValue"] = value,
            ["fuelEndPercent"] = 35,
            ["endPhoto"] = "sha256:" + Hex(photo),
        });

    /// <summary>A small JPEG-looking photo, different each time.</summary>
    private static byte[] Photo() => [0xFF, 0xD8, 0xFF, 0xE0, .. RandomNumberGenerator.GetBytes(64), 0xFF, 0xD9];

    private static string Hex(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>The tablet uploads a photo as it does before sending the command that names it (ADR-0032 §2).</summary>
    private static async Task UploadAsync(TestTablet tablet, byte[] content)
    {
        using var presigned = await tablet.SendAsync(HttpMethod.Post, "/api/v1/sync/blobs/presign", new { sha256 = Hex(content), size = content.Length, contentType = "image/jpeg" });
        presigned.StatusCode.Should().Be(HttpStatusCode.OK, await presigned.Content.ReadAsStringAsync(Ct));
        var upload = await presigned.Content.ReadFromJsonAsync<JsonElement>(Ct);
        using var http = new HttpClient();
        using var body = new ByteArrayContent(content);
        using var request = new HttpRequestMessage(HttpMethod.Put, upload.GetProperty("url").GetString()) { Content = body };
        foreach (var header in upload.GetProperty("headers").EnumerateObject())
        {
            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()))
            {
                body.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
            }
        }

        using var put = await http.SendAsync(request, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token) => await host.SendAsync(HttpMethod.Get, path, token);
}
