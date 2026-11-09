using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Karry.BuildingBlocks.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.IntegrationTests;

/// <summary>
/// The evidence bucket (ADR-0032 §4): created with Object Lock in compliance mode, so a stored blob cannot be deleted
/// or replaced before its retention ends, and with a CORS rule for the field app, which uploads from the browser.
/// </summary>
public sealed class EvidenceStoreTests(SeaweedFs seaweed) : IClassFixture<SeaweedFs>
{
    private const string FieldOrigin = "http://*.localhost:5174";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_blob_is_retained_under_compliance_lock_and_cannot_be_deleted_or_replaced()
    {
        var (store, bucket) = Store();
        using var _ = store;
        var photo = Photo();
        var key = Key(photo);
        (await store.HeadAsync(key, Ct)).Should().BeNull("the first call creates the bucket");

        (await PutAsync(store.PresignPut(key, Checksum(photo), "image/jpeg", DateTimeOffset.UtcNow.AddMinutes(5)), photo)).Should().Be(HttpStatusCode.OK);

        using var s3 = Admin();
        var config = await s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = bucket }, Ct);
        config.ObjectLockConfiguration.Rule.DefaultRetention.Mode.Should().Be(ObjectLockRetentionMode.Compliance);
        config.ObjectLockConfiguration.Rule.DefaultRetention.Days.Should().Be(30);
        var head = await s3.GetObjectMetadataAsync(bucket, key, Ct);
        head.ObjectLockMode.Should().Be(ObjectLockMode.Compliance);
        var delete = () => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = head.VersionId }, Ct);
        (await delete.Should().ThrowAsync<AmazonS3Exception>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "replaced" }, Ct);
        using var original = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = head.VersionId }, Ct);
        using var content = new MemoryStream();
        await original.ResponseStream.CopyToAsync(content, Ct);
        content.ToArray().Should().Equal(photo, "a replacement is a new version; the retained one stays readable");
    }

    [Fact]
    public async Task The_field_app_s_origins_may_upload_from_the_browser_and_no_other()
    {
        var (store, _) = Store();
        using var __ = store;
        var photo = Photo();
        var put = store.PresignPut(Key(photo), Checksum(photo), "image/jpeg", DateTimeOffset.UtcNow.AddMinutes(5));
        (await store.HeadAsync(Key(photo), Ct)).Should().BeNull("the first call creates the bucket");

        (await PreflightAsync(put.Url, "http://carriere-nord.localhost:5174")).Should().Be("http://carriere-nord.localhost:5174");
        (await PreflightAsync(put.Url, "http://evil.example")).Should().BeNull();
    }

    [Fact]
    public async Task The_retention_and_origins_are_read_from_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Evidence:Bucket"] = "evidence",
            ["Storage:Evidence:RetentionDays"] = "3650",
            ["Storage:Evidence:AllowedOrigins:0"] = FieldOrigin,
            ["Storage:Evidence:AllowedOrigins:1"] = "https://*.field.karry.test",
        }).Build();

        var options = new ServiceCollection().AddEvidenceStore(configuration).BuildServiceProvider().GetRequiredService<EvidenceStoreOptions>();

        options.RetentionDays.Should().Be(3650);
        options.AllowedOrigins.Should().Equal(FieldOrigin, "https://*.field.karry.test");
        options.UploadValidFor.Should().Be(TimeSpan.FromMinutes(15));
    }

    private (S3EvidenceStore Store, string Bucket) Store()
    {
        var bucket = $"evidence-{Guid.NewGuid():N}"[..24];
        var store = new S3EvidenceStore(
            new ObjectStoreOptions { ServiceUrl = seaweed.ServiceUrl, AccessKey = SeaweedFs.AccessKey, SecretKey = SeaweedFs.SecretKey },
            new EvidenceStoreOptions { Bucket = bucket, CreateBucketIfMissing = true, RetentionDays = 30, AllowedOrigins = [FieldOrigin] });
        return (store, bucket);
    }

    private AmazonS3Client Admin() => new(
        new BasicAWSCredentials(SeaweedFs.AccessKey, SeaweedFs.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = seaweed.ServiceUrl,
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    private static byte[] Photo() => [0xFF, 0xD8, 0xFF, .. RandomNumberGenerator.GetBytes(64)];

    private static string Key(byte[] content) => EvidenceKeys.For(Guid.Empty, Guid.Empty, Convert.ToHexStringLower(SHA256.HashData(content)));

    private static string Checksum(byte[] content) => Convert.ToBase64String(SHA256.HashData(content));

    private static async Task<HttpStatusCode> PutAsync(PresignedPut put, byte[] content)
    {
        using var http = new HttpClient();
        using var body = new ByteArrayContent(content);
        using var request = new HttpRequestMessage(HttpMethod.Put, put.Url) { Content = body };
        foreach (var (name, value) in put.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                body.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private static async Task<string?> PreflightAsync(Uri url, string origin)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-amz-checksum-sha256");
        using var response = await http.SendAsync(request, Ct);
        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed) ? allowed.Single() : null;
    }
}
