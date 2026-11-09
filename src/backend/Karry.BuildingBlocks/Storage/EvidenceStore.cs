using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.BuildingBlocks.Storage;

/// <summary>What the store holds under a key: size, SHA-256 (base64, as the store computed it) and media type.</summary>
public sealed record BlobHead(long Size, string? Sha256, string ContentType);

/// <summary>
/// A URL the tablet <c>PUT</c>s one blob to, with the headers it must send exactly as given (they are signed: another
/// checksum or type is refused by the store).
/// </summary>
public sealed record PresignedPut(Uri Url, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Evidence blobs (photos, signature strokes; ADR-0032): uploaded by the tablet straight to the store with a pre-signed
/// <c>PUT</c> that names the content's SHA-256, so the store refuses other bytes; read back by <c>HEAD</c> when the
/// command that uses the blob is handled. Keys are built by the server, never taken from the request.
/// </summary>
public interface IEvidenceStore
{
    /// <summary>False when no evidence bucket is configured (the endpoints answer 503).</summary>
    bool Available { get; }

    /// <summary>The object's size, checksum and type, or null when there is none under <paramref name="key"/>.</summary>
    Task<BlobHead?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>A pre-signed <c>PUT</c> for <paramref name="key"/>, only for content of this SHA-256 (base64) and type.</summary>
    PresignedPut PresignPut(string key, string sha256, string contentType, DateTimeOffset expiresAt);
}

public static class EvidenceKeys
{
    /// <summary><c>tenants/{tenant}/devices/{device}/evidence/{sha256 hex}</c>: content-addressed, per tablet (ADR-0032 §1).</summary>
    public static string For(Guid tenant, Guid device, string sha256Hex) => $"tenants/{tenant:D}/devices/{device:D}/evidence/{sha256Hex}";
}

/// <summary>
/// <c>Storage:Evidence</c>. The endpoint and keys are those of <c>Storage</c>; the bucket is separate because it is
/// retained under Object Lock (ADR-0032 §4).
/// </summary>
public sealed class EvidenceStoreOptions
{
    public const string Section = "Storage:Evidence";

    public string? Bucket { get; set; }

    /// <summary>
    /// The store's address as the tablet reaches it, which pre-signed URLs carry (the host is signed). Defaults to
    /// <c>Storage:ServiceUrl</c>, which only works when the API and the tablets see the store at the same address.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>How long a pre-signed URL can be used (ADR-0032 §2: 15 minutes).</summary>
    public TimeSpan UploadValidFor { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Creates the bucket on first use (local stack, CI), with Object Lock and the CORS rule below. In production the
    /// bucket is infrastructure as code with the same settings.
    /// </summary>
    public bool CreateBucketIfMissing { get; set; }

    /// <summary>
    /// The bucket's default retention, in compliance mode (ADR-0032 §4): nobody, administrators included, can delete or
    /// shorten it before it ends. 5 years until the owner gives the legal period (WP-P0-06); it can only go up.
    /// </summary>
    public int RetentionDays { get; set; } = 1826;

    /// <summary>
    /// Origins of the field app, which <c>PUT</c>s from the browser to the store (CORS). One <c>*</c> per origin is
    /// allowed, for the companies' addresses (<c>http://*.localhost:5174</c> locally).
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; set; } = [];
}

public static class EvidenceStoreRegistration
{
    /// <summary>The S3 store when <c>Storage:Evidence:Bucket</c> is set, else one that reports itself unavailable. A store registered before wins (tests).</summary>
    public static IServiceCollection AddEvidenceStore(this IServiceCollection services, IConfiguration configuration)
    {
        var storage = new ObjectStoreOptions();
        configuration.GetSection(ObjectStoreOptions.Section).Bind(storage);
        var evidence = new EvidenceStoreOptions();
        configuration.GetSection(EvidenceStoreOptions.Section).Bind(evidence);
        services.TryAddSingleton(evidence);
        if (string.IsNullOrWhiteSpace(evidence.Bucket))
        {
            services.TryAddSingleton<IEvidenceStore, UnavailableEvidenceStore>();
        }
        else
        {
            services.TryAddSingleton<IEvidenceStore>(_ => new S3EvidenceStore(storage, evidence));
        }

        return services;
    }
}

internal sealed class UnavailableEvidenceStore : IEvidenceStore
{
    public bool Available => false;

    public Task<BlobHead?> HeadAsync(string key, CancellationToken cancellationToken) => throw Unavailable();

    public PresignedPut PresignPut(string key, string sha256, string contentType, DateTimeOffset expiresAt) => throw Unavailable();

    private static InvalidOperationException Unavailable() => new($"{EvidenceStoreOptions.Section}:Bucket is not configured.");
}

/// <summary>Evidence in one S3 bucket. Signing a URL is local (no call to the store); <c>HEAD</c> asks for the checksum.</summary>
public sealed class S3EvidenceStore : IEvidenceStore, IDisposable
{
    public const string ChecksumHeader = "x-amz-checksum-sha256";

    private readonly string bucket;
    private readonly AmazonS3Client client;
    private readonly AmazonS3Client signer;
    private readonly Protocol protocol;
    private readonly EvidenceStoreOptions evidence;
    private readonly SemaphoreSlim bucketCheck = new(1, 1);
    private bool bucketReady;

    public S3EvidenceStore(ObjectStoreOptions storage, EvidenceStoreOptions evidence)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(evidence);
        bucket = evidence.Bucket ?? throw new ArgumentException("The evidence bucket is not set.", nameof(evidence));
        bucketReady = !evidence.CreateBucketIfMissing;
        this.evidence = evidence;
        client = Client(storage, storage.ServiceUrl);
        var publicUrl = string.IsNullOrWhiteSpace(evidence.PublicUrl) ? storage.ServiceUrl : evidence.PublicUrl;
        signer = Client(storage, publicUrl);
        protocol = publicUrl is not null && publicUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS;
    }

    public bool Available => true;

    public Task<BlobHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
        S3ObjectStore.CallAsync(async token =>
        {
            await EnsureBucketAsync(token).ConfigureAwait(false);
            try
            {
                var head = await client.GetObjectMetadataAsync(
                    new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED }, token).ConfigureAwait(false);
                return new BlobHead(head.ContentLength, head.ChecksumSHA256, head.Headers.ContentType);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (BlobHead?)null;
            }
        }, cancellationToken);

    public PresignedPut PresignPut(string key, string sha256, string contentType, DateTimeOffset expiresAt)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            ContentType = contentType,
            Protocol = protocol,
        };

        // In the request's headers, the checksum is signed: the store refuses a PUT without it, with another value,
        // or whose bytes do not hash to it (PresignedChecksumTests).
        request.Headers[ChecksumHeader] = sha256;
        var url = signer.GetPreSignedURL(request);
        return new PresignedPut(new Uri(url), expiresAt, new Dictionary<string, string>
        {
            ["Content-Type"] = contentType,
            [ChecksumHeader] = sha256,
        });
    }

    public void Dispose()
    {
        client.Dispose();
        signer.Dispose();
        bucketCheck.Dispose();
    }

    private static AmazonS3Client Client(ObjectStoreOptions storage, string? serviceUrl)
    {
        return new AmazonS3Client(new BasicAWSCredentials(storage.AccessKey, storage.SecretKey), new AmazonS3Config
        {
            ServiceURL = string.IsNullOrWhiteSpace(serviceUrl) ? null : serviceUrl,
            AuthenticationRegion = storage.Region,
            ForcePathStyle = !string.IsNullOrWhiteSpace(serviceUrl),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = S3ObjectStore.AttemptTimeout,
            MaxErrorRetry = 1,
        });
    }

    /// <summary>
    /// Object Lock can only be turned on when the bucket is created; the retention applies to every new object. An
    /// existing bucket is left as it is (its retention is never lowered by a setting).
    /// </summary>
    private async Task CreateBucketAsync(CancellationToken cancellationToken)
    {
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket, ObjectLockEnabledForBucket = true }, cancellationToken).ConfigureAwait(false);
        await client.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = bucket,
            ObjectLockConfiguration = new ObjectLockConfiguration
            {
                ObjectLockEnabled = ObjectLockEnabled.Enabled,
                Rule = new ObjectLockRule { DefaultRetention = new DefaultRetention { Mode = ObjectLockRetentionMode.Compliance, Days = evidence.RetentionDays } },
            },
        }, cancellationToken).ConfigureAwait(false);
        if (evidence.AllowedOrigins.Count > 0)
        {
            await client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
            {
                BucketName = bucket,
                Configuration = new CORSConfiguration
                {
                    Rules =
                    [
                        new CORSRule
                        {
                            AllowedOrigins = [.. evidence.AllowedOrigins],
                            AllowedMethods = ["PUT"],
                            AllowedHeaders = ["content-type", ChecksumHeader],
                            MaxAgeSeconds = 3600,
                        },
                    ],
                },
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (bucketReady)
        {
            return;
        }

        await bucketCheck.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var buckets = await client.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
            if (!(buckets.Buckets ?? []).Any(b => b.BucketName == bucket))
            {
                await CreateBucketAsync(cancellationToken).ConfigureAwait(false);
            }

            bucketReady = true;
        }
        finally
        {
            bucketCheck.Release();
        }
    }
}
