using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Karry.Testing;
using Npgsql;

namespace Karry.Host.IntegrationTests;

/// <summary>The sync host with an evidence bucket in SeaweedFS, as the local stack (ADR-0032).</summary>
public sealed class BlobSyncHost : SyncHost
{
    public const string Bucket = "karry-evidence-test";

    private SeaweedFsServer? storage;

    /// <summary>The store as an administrator sees it, to write around the API (a forged blob).</summary>
    public AmazonS3Client StoreAdmin() => new(
        new BasicAWSCredentials(SeaweedFsServer.AccessKey, SeaweedFsServer.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = storage!.ServiceUrl,
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (storage is not null)
        {
            await storage.DisposeAsync();
        }
    }

    protected override async Task<IReadOnlyDictionary<string, string?>> StartStorageAsync()
    {
        storage = await SeaweedFsServer.StartAsync();
        return new Dictionary<string, string?>
        {
            ["Storage:ServiceUrl"] = storage.ServiceUrl,
            ["Storage:AccessKey"] = SeaweedFsServer.AccessKey,
            ["Storage:SecretKey"] = SeaweedFsServer.SecretKey,
            ["Storage:Evidence:Bucket"] = Bucket,
            ["Storage:Evidence:CreateBucketIfMissing"] = "true",
        };
    }
}

/// <summary>
/// Evidence blobs (ADR-0032 §2, US-P2-OFF-01): the tablet asks for a pre-signed <c>PUT</c> for its content and uploads
/// the bytes straight to the store, which refuses anything but those bytes at that key (TC-P2-SYNC-012).
/// </summary>
[Trait("Story", "US-P2-OFF-01")]
public sealed class SyncBlobsTests(BlobSyncHost host) : IClassFixture<BlobSyncHost>
{
    private const string Presign = "/api/v1/sync/blobs/presign";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_tablet_uploads_a_blob_with_the_url_it_is_given_and_asking_again_answers_that_it_exists()
    {
        using var tablet = await host.TabletAsync(host.A);
        var photo = Photo();

        var upload = await PresignAsync(tablet, photo);

        upload.GetProperty("exists").GetBoolean().Should().BeFalse();
        upload.GetProperty("key").GetString().Should().Be($"tenants/{host.A.Id:D}/devices/{tablet.Id:D}/evidence/{Hex(photo)}");
        DateTimeOffset.Parse(upload.GetProperty("expiresAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(host.Clock.GetUtcNow() + TimeSpan.FromMinutes(15));
        using (var put = await PutAsync(upload, photo))
        {
            put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        }

        var again = await PresignAsync(tablet, photo);
        again.GetProperty("exists").GetBoolean().Should().BeTrue();
        again.GetProperty("url").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SYNC-012")]
    public async Task The_url_is_good_only_for_its_own_bytes_and_key()
    {
        using var tablet = await host.TabletAsync(host.A);
        var photo = Photo();
        var upload = await PresignAsync(tablet, photo);

        using (var otherBytes = await PutAsync(upload, Photo()))
        {
            otherBytes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await otherBytes.Content.ReadAsStringAsync(Ct)).Should().Contain("BadDigest");
        }

        var url = upload.GetProperty("url").GetString()!;
        var otherKey = url.Replace(Hex(photo), Hex(Photo()), StringComparison.Ordinal);
        using (var elsewhere = await PutAsync(upload, photo, otherKey))
        {
            elsewhere.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await PresignAsync(tablet, photo)).GetProperty("exists").GetBoolean().Should().BeFalse();
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SYNC-012")]
    public async Task Each_tablet_and_company_gets_its_own_key_for_the_same_content()
    {
        using var first = await host.TabletAsync(host.A);
        using var second = await host.TabletAsync(host.A);
        using var other = await host.TabletAsync(host.B);
        var photo = Photo();

        using (await PutAsync(await PresignAsync(first, photo), photo))
        {
        }

        var keys = new List<string>();
        foreach (var tablet in new[] { second, other })
        {
            var upload = await PresignAsync(tablet, photo);
            upload.GetProperty("exists").GetBoolean().Should().BeFalse("another tablet's upload is not this tablet's evidence");
            keys.Add(upload.GetProperty("key").GetString()!);
        }

        keys.Should().Equal(
            $"tenants/{host.A.Id:D}/devices/{second.Id:D}/evidence/{Hex(photo)}",
            $"tenants/{host.B.Id:D}/devices/{other.Id:D}/evidence/{Hex(photo)}");
    }

    [Fact]
    public async Task A_blob_too_large_of_another_type_or_with_a_malformed_hash_is_refused()
    {
        using var tablet = await host.TabletAsync(host.A);
        var hash = Hex(Photo());

        foreach (var body in new object[]
        {
            new { sha256 = hash, size = 1024 * 1024 + 1, contentType = "image/jpeg" },
            new { sha256 = hash, size = 0, contentType = "image/jpeg" },
            new { sha256 = hash, size = 10, contentType = "application/pdf" },
            new { sha256 = hash.ToUpperInvariant(), size = 10, contentType = "image/jpeg" },
            new { sha256 = "../" + hash[3..], size = 10, contentType = "image/jpeg" },
            new { sha256 = (string?)null, size = 10, contentType = "image/jpeg" },
        })
        {
            using var response = await tablet.SendAsync(HttpMethod.Post, Presign, body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().Should().Be("request.invalid");
        }
    }

    [Fact]
    public async Task Only_an_enrolled_tablet_that_is_not_revoked_gets_a_url()
    {
        var photo = Photo();
        var body = new { sha256 = Hex(photo), size = photo.Length, contentType = "image/jpeg" };
        using var tablet = await host.TabletAsync(host.A);
        (await host.SendAsync(HttpMethod.Post, $"{TestTablet.Devices}/{tablet.Id}/revoke", host.Admin(host.A))).EnsureSuccessStatusCode();

        using var admin = await host.SendAsync(HttpMethod.Post, Presign, host.Admin(host.A), body);
        using var revoked = await tablet.SendAsync(HttpMethod.Post, Presign, body);

        admin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SYNC-004")]
    public async Task A_command_sent_before_its_blob_is_rejected_retryable_and_accepted_once_the_blob_is_uploaded()
    {
        using var tablet = await host.TabletAsync(host.A);
        var photo = Photo();
        var command = tablet.Command(1, WithEvidence(photo));

        var early = await UploadAsync(tablet, command);
        early.GetProperty("status").GetString().Should().Be("rejected");
        early.GetProperty("code").GetString().Should().Be("sync.blob_missing");
        early.GetProperty("retryable").GetBoolean().Should().BeTrue();
        (await EvidenceAsync(tablet.Id)).Should().BeEmpty();

        using (await PutAsync(await PresignAsync(tablet, photo), photo))
        {
        }

        (await UploadAsync(tablet, command)).GetProperty("status").GetString().Should().Be("accepted");
        (await EvidenceAsync(tablet.Id)).Should().Equal((Guid.Parse(command["commandId"]!.GetValue<string>()), Hex(photo), (long)photo.Length, "image/jpeg"));
    }

    [Fact]
    [Trait("TestCase", "TC-P2-SYNC-005")]
    public async Task A_command_whose_blob_was_replaced_around_the_api_is_rejected_for_good()
    {
        using var tablet = await host.TabletAsync(host.A);
        var photo = Photo();
        using (var s3 = host.StoreAdmin())
        {
            (await PresignAsync(tablet, Photo())).GetProperty("exists").GetBoolean().Should().BeFalse("the first call creates the bucket");
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = BlobSyncHost.Bucket,
                Key = $"tenants/{host.A.Id:D}/devices/{tablet.Id:D}/evidence/{Hex(photo)}",
                InputStream = new MemoryStream(Photo()),
                ContentType = "image/jpeg",
            }, Ct);
        }

        var receipt = await UploadAsync(tablet, tablet.Command(1, WithEvidence(photo)));

        receipt.GetProperty("status").GetString().Should().Be("rejected");
        receipt.GetProperty("code").GetString().Should().Be("sync.blob_mismatch");
        receipt.GetProperty("retryable").GetBoolean().Should().BeFalse();
        (await EvidenceAsync(tablet.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_command_with_malformed_evidence_is_rejected_for_good()
    {
        using var tablet = await host.TabletAsync(host.A);

        var receipt = await UploadAsync(tablet, tablet.Command(1, new { evidence = new[] { new { sha256 = "abc", size = 10, contentType = "image/jpeg" } } }));

        receipt.GetProperty("code").GetString().Should().Be("sync.evidence_invalid");
        receipt.GetProperty("retryable").GetBoolean().Should().BeFalse();
    }

    private static object WithEvidence(byte[] photo) =>
        new { n = 1, evidence = new[] { new { sha256 = Hex(photo), size = photo.Length, contentType = "image/jpeg" } } };

    /// <summary>Uploads one command and answers its receipt.</summary>
    private static async Task<JsonElement> UploadAsync(TestTablet tablet, JsonObject command)
    {
        using var response = await tablet.SendAsync(HttpMethod.Post, "/api/v1/sync/batches", new { commands = new[] { command } });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("results")[0];
    }

    private async Task<List<(Guid Command, string Sha256, long Size, string ContentType)>> EvidenceAsync(Guid deviceId)
    {
        await using var connection = new NpgsqlConnection(host.Database.Admin);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand("SELECT command_id, sha256, size, content_type FROM sync.command_evidence WHERE device_id = $1", connection)
        {
            Parameters = { new() { Value = deviceId } },
        };
        await using var reader = await query.ExecuteReaderAsync(Ct);
        var rows = new List<(Guid, string, long, string)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>A small JPEG-like blob, different each time.</summary>
    private static byte[] Photo() => [0xFF, 0xD8, 0xFF, .. RandomNumberGenerator.GetBytes(64)];

    private static string Hex(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static async Task<JsonElement> PresignAsync(TestTablet tablet, byte[] content)
    {
        using var response = await tablet.SendAsync(HttpMethod.Post, Presign, new { sha256 = Hex(content), size = content.Length, contentType = "image/jpeg" });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>The tablet's <c>PUT</c> to the store: the bytes, with the headers it was given.</summary>
    private static async Task<HttpResponseMessage> PutAsync(JsonElement upload, byte[] content, string? url = null)
    {
        using var http = new HttpClient();
        using var body = new ByteArrayContent(content);
        using var request = new HttpRequestMessage(HttpMethod.Put, url ?? upload.GetProperty("url").GetString()) { Content = body };
        foreach (var header in upload.GetProperty("headers").EnumerateObject())
        {
            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()))
            {
                body.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
            }
        }

        var response = await http.SendAsync(request, Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }
}
