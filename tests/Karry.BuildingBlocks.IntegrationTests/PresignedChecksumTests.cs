using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Karry.BuildingBlocks.IntegrationTests;

/// <summary>
/// P2.2 spike (ADR-0032): what the storage does with a pre-signed PUT that names its content's SHA-256 in the signed
/// header <c>x-amz-checksum-sha256</c>. The tablet uploads evidence photos straight to the bucket with such a URL; these
/// tests pin down whether the bucket itself refuses bytes that do not match, or whether the server must check them.
/// </summary>
public sealed class PresignedChecksumTests(SeaweedFs seaweed) : IClassFixture<SeaweedFs>
{
    private const string ChecksumHeader = "x-amz-checksum-sha256";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_put_whose_bytes_match_the_signed_checksum_is_stored()
    {
        using var s3 = Client();
        var (bucket, key) = await NewKeyAsync(s3);
        byte[] photo = [0xFF, 0xD8, 0xFF, 1, 2, 3];

        using var response = await PutAsync(Presign(s3, bucket, key, Sha256(photo)), photo, Sha256(photo));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(s3, bucket, key)).Should().Equal(photo);
    }

    [Fact]
    public async Task A_put_whose_bytes_differ_from_the_signed_checksum_is_refused_and_nothing_is_stored()
    {
        using var s3 = Client();
        var (bucket, key) = await NewKeyAsync(s3);
        byte[] photo = [0xFF, 0xD8, 0xFF, 1, 2, 3];
        byte[] other = [0xFF, 0xD8, 0xFF, 9, 9, 9];

        using var response = await PutAsync(Presign(s3, bucket, key, Sha256(photo)), other, Sha256(photo));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("<Code>BadDigest</Code>");
        (await ReadAsync(s3, bucket, key)).Should().BeNull();
    }

    [Fact]
    public async Task A_put_that_changes_the_signed_checksum_header_is_refused()
    {
        using var s3 = Client();
        var (bucket, key) = await NewKeyAsync(s3);
        byte[] photo = [0xFF, 0xD8, 0xFF, 1, 2, 3];
        byte[] other = [0xFF, 0xD8, 0xFF, 9, 9, 9];

        using var response = await PutAsync(Presign(s3, bucket, key, Sha256(photo)), other, Sha256(other));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("<Code>SignatureDoesNotMatch</Code>");
        (await ReadAsync(s3, bucket, key)).Should().BeNull();
    }

    [Fact]
    public async Task A_head_in_checksum_mode_returns_the_stored_size_and_checksum()
    {
        using var s3 = Client();
        var (bucket, key) = await NewKeyAsync(s3);
        byte[] photo = [0xFF, 0xD8, 0xFF, 1, 2, 3];
        using (await PutAsync(Presign(s3, bucket, key, Sha256(photo)), photo, Sha256(photo)))
        {
        }

        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED }, Ct);

        head.ContentLength.Should().Be(photo.Length);
        head.ChecksumSHA256.Should().Be(Sha256(photo));
    }

    [Fact]
    public async Task An_expired_url_is_refused()
    {
        using var s3 = Client();
        var (bucket, key) = await NewKeyAsync(s3);
        byte[] photo = [0xFF, 0xD8, 0xFF, 1, 2, 3];

        var url = Presign(s3, bucket, key, Sha256(photo), DateTime.UtcNow.AddSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        using var response = await PutAsync(url, photo, Sha256(photo));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync(Ct));
        (await ReadAsync(s3, bucket, key)).Should().BeNull();
    }

    private AmazonS3Client Client() => new(
        new BasicAWSCredentials(SeaweedFs.AccessKey, SeaweedFs.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = seaweed.ServiceUrl,
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    private static async Task<(string Bucket, string Key)> NewKeyAsync(AmazonS3Client s3)
    {
        var bucket = $"evidence-{Guid.NewGuid():N}"[..24];
        await s3.PutBucketAsync(bucket, Ct);
        return (bucket, $"tenants/{Guid.NewGuid():D}/evidence/{Guid.NewGuid():D}");
    }

    private static string Presign(AmazonS3Client s3, string bucket, string key, string checksum, DateTime? expires = null)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            Protocol = Protocol.HTTP,
        };
        request.Headers[ChecksumHeader] = checksum;
        return s3.GetPreSignedURL(request);
    }

    private static async Task<HttpResponseMessage> PutAsync(string url, byte[] content, string checksum)
    {
        using var http = new HttpClient();
        using var body = new ByteArrayContent(content);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = body };
        request.Headers.TryAddWithoutValidation(ChecksumHeader, checksum);
        var response = await http.SendAsync(request, Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    private static async Task<byte[]?> ReadAsync(AmazonS3Client s3, string bucket, string key)
    {
        try
        {
            using var response = await s3.GetObjectAsync(bucket, key, Ct);
            using var content = new MemoryStream();
            await response.ResponseStream.CopyToAsync(content, Ct);
            return content.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string Sha256(byte[] content) => Convert.ToBase64String(SHA256.HashData(content));
}
