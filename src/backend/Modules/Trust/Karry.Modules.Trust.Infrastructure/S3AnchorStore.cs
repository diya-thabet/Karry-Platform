using System.Runtime.CompilerServices;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Karry.Modules.Trust.Application;
using Karry.Modules.Trust.Domain;
using Karry.SharedKernel.Tenancy;

namespace Karry.Modules.Trust.Infrastructure;

/// <summary>
/// Anchors in an S3 bucket created with Object Lock (ADR-0017): every object is written with COMPLIANCE retention, so
/// neither Karry nor the storage account can alter or delete it before the retention date. SeaweedFS locally.
/// </summary>
public sealed class S3AnchorStore(AnchoringOptions options, TimeProvider clock) : IAnchorStore, IDisposable
{
    private readonly AmazonS3Client client = new(
        new BasicAWSCredentials(options.AccessKey, options.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = string.IsNullOrWhiteSpace(options.ServiceUrl) ? null : options.ServiceUrl,
            AuthenticationRegion = options.Region,
            ForcePathStyle = !string.IsNullOrWhiteSpace(options.ServiceUrl),
            // Checksums only where S3 requires them (Object Lock puts): S3-compatible stores may not accept trailers.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    /// <summary>Local development: create the bucket with Object Lock enabled (it cannot be enabled later on most stores).</summary>
    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        var buckets = await client.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        if ((buckets.Buckets ?? []).Any(b => b.BucketName == options.Bucket))
        {
            return;
        }

        await client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket, ObjectLockEnabledForBucket = true }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PutAsync(LedgerAnchor anchor, string document, CancellationToken cancellationToken)
    {
        if (options.CreateBucketIfMissing)
        {
            await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);
        }

        ArgumentNullException.ThrowIfNull(anchor);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = anchor.ObjectKey,
            ContentBody = document,
            ContentType = "application/json",
            ObjectLockMode = ObjectLockMode.Compliance,
            ObjectLockRetainUntilDate = clock.GetUtcNow().AddDays(options.RetentionDays).UtcDateTime,
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StoredAnchor> ListAsync(TenantId tenant, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var versions = new List<S3ObjectVersion>();
        string? keyMarker = null;
        string? versionMarker = null;
        do
        {
            var page = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = options.Bucket,
                Prefix = $"{tenant}/",
                KeyMarker = keyMarker,
                VersionIdMarker = versionMarker,
            }, cancellationToken).ConfigureAwait(false);
            versions.AddRange((page.Versions ?? []).Where(v => v.IsDeleteMarker != true));
            (keyMarker, versionMarker) = page.IsTruncated == true ? (page.NextKeyMarker, page.NextVersionIdMarker) : (null, null);
        }
        while (keyMarker is not null);

        foreach (var key in versions.GroupBy(v => v.Key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // S3 lists a key's versions newest first: the original is the last one (timestamps have only second precision).
            var original = key.Last();
            using var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = options.Bucket,
                Key = key.Key,
                VersionId = original.VersionId,
            }, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
            yield return new StoredAnchor(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), key.Count());
        }
    }

    public void Dispose() => client.Dispose();
}
