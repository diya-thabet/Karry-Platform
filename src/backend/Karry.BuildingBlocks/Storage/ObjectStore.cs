using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.BuildingBlocks.Storage;

/// <summary>A stored file: its bytes and media type.</summary>
public sealed record StoredObject(byte[] Content, string ContentType);

/// <summary>
/// Files of the company kept out of the database (photos, documents): S3-compatible object storage (SeaweedFS locally,
/// ADR-0026). Keys start with the tenant (<see cref="ObjectKeys.ForTenant"/>); the module checks access before reading.
/// </summary>
public interface IObjectStore
{
    /// <summary>False when no storage is configured (the endpoints answer 503 instead of failing later).</summary>
    bool Available { get; }

    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken);

    /// <summary>The object, or null when there is none under <paramref name="key"/>.</summary>
    Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes the object; no error when it is already gone.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public static class ObjectKeys
{
    /// <summary><c>tenants/{tenant}/{module}/{path}</c>: one prefix per company, so its files can be listed, exported or erased together.</summary>
    public static string ForTenant(Guid tenant, string module, string path) => $"tenants/{tenant:D}/{module}/{path}";
}

/// <summary>
/// The storage is configured but does not answer (stopped, unreachable, overloaded): a temporary state, which the API
/// reports as 503 <c>server.unavailable</c> with <c>Retry-After</c> (hardening plan H-R4), never as an unexpected error.
/// </summary>
public sealed class ObjectStoreUnavailableException : Exception
{
    public ObjectStoreUnavailableException()
    {
    }

    public ObjectStoreUnavailableException(string message)
        : base(message)
    {
    }

    public ObjectStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Section <c>Storage</c>: the bucket for company files (not the anchor bucket, which has Object Lock).</summary>
public sealed class ObjectStoreOptions
{
    public const string Section = "Storage";

    /// <summary>S3-compatible endpoint (local SeaweedFS); empty for AWS S3.</summary>
    public string? ServiceUrl { get; set; }

    public string? Bucket { get; set; }

    public string Region { get; set; } = "us-east-1";

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Local development: create the bucket on first use.</summary>
    public bool CreateBucketIfMissing { get; set; }
}

public static class ObjectStoreRegistration
{
    /// <summary>The S3 store when <c>Storage:Bucket</c> is set, else one that reports itself unavailable. A store registered before wins (tests).</summary>
    public static IServiceCollection AddObjectStore(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ObjectStoreOptions();
        configuration.GetSection(ObjectStoreOptions.Section).Bind(options);
        if (string.IsNullOrWhiteSpace(options.Bucket))
        {
            services.TryAddSingleton<IObjectStore, UnavailableObjectStore>();
        }
        else
        {
            services.TryAddSingleton<IObjectStore>(_ => new S3ObjectStore(options));
        }

        return services;
    }
}

internal sealed class UnavailableObjectStore : IObjectStore
{
    public bool Available => false;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) => throw Unavailable();

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken) => throw Unavailable();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw Unavailable();

    private static InvalidOperationException Unavailable() => new($"{ObjectStoreOptions.Section}:Bucket is not configured.");
}

/// <summary>Objects in one S3 bucket, private (no public URL): the API streams them after its own access checks.</summary>
public sealed class S3ObjectStore(ObjectStoreOptions options) : IObjectStore, IDisposable
{
    private readonly AmazonS3Client client = new(
        new BasicAWSCredentials(options.AccessKey, options.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = string.IsNullOrWhiteSpace(options.ServiceUrl) ? null : options.ServiceUrl,
            AuthenticationRegion = options.Region,
            ForcePathStyle = !string.IsNullOrWhiteSpace(options.ServiceUrl),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,

            // A stopped or frozen storage must answer the user within seconds, not after the SDK's 100 s timeout and four
            // retries (hardening plan H-R4): one retry, 10 s per attempt (a 5 MB photo on a slow link fits easily).
            Timeout = AttemptTimeout,
            MaxErrorRetry = 1,
        });

    /// <summary>The longest one attempt may take (the SDK's timeout).</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest one call may take, retries included, before the storage counts as unavailable.</summary>
    public static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim bucketCheck = new(1, 1);
    private bool bucketReady = !options.CreateBucketIfMissing;

    public bool Available => true;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
        CallAsync(async token =>
        {
            await EnsureBucketAsync(token).ConfigureAwait(false);
            using var body = new MemoryStream(content);
            await client.PutObjectAsync(new PutObjectRequest { BucketName = options.Bucket, Key = key, InputStream = body, ContentType = contentType }, token)
                .ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken) =>
        CallAsync(async token =>
        {
            await EnsureBucketAsync(token).ConfigureAwait(false);
            try
            {
                using var response = await client.GetObjectAsync(options.Bucket, key, token).ConfigureAwait(false);
                using var content = new MemoryStream();
                await response.ResponseStream.CopyToAsync(content, token).ConfigureAwait(false);
                return new StoredObject(content.ToArray(), response.Headers.ContentType);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        CallAsync(async token =>
        {
            await EnsureBucketAsync(token).ConfigureAwait(false);
            await client.DeleteObjectAsync(options.Bucket, key, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public void Dispose()
    {
        client.Dispose();
        bucketCheck.Dispose();
    }

    /// <summary>
    /// A storage that cannot be reached, times out or answers 5xx is unavailable; anything else (a refused request, a
    /// missing bucket) stays a fault of ours. The caller's own cancellation stays a cancellation.
    /// </summary>
    internal static async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        // One deadline for the whole call, retries included: the SDK's own timeouts bound each attempt, not the sum
        // (on a slow CI runner a frozen storage took 31 s). WaitAsync holds it even if the SDK ignores the cancellation.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CallBudget);
        try
        {
            return await call(budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (budget.IsCancellationRequested || IsOutage(ex)))
        {
            throw new ObjectStoreUnavailableException("Object storage is not answering.", ex);
        }
    }

    private static bool IsOutage(Exception exception) => exception switch
    {
        AmazonServiceException { StatusCode: >= System.Net.HttpStatusCode.InternalServerError } => true,
        AmazonServiceException { StatusCode: 0 } service => service.InnerException is not null && IsOutage(service.InnerException),
        AmazonServiceException => false,
        HttpRequestException or TimeoutException or TaskCanceledException or IOException or System.Net.Sockets.SocketException => true,
        AmazonClientException client => client.InnerException is null || IsOutage(client.InnerException),
        _ => false,
    };

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
            if (!(buckets.Buckets ?? []).Any(b => b.BucketName == options.Bucket))
            {
                await client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, cancellationToken).ConfigureAwait(false);
            }

            bucketReady = true;
        }
        finally
        {
            bucketCheck.Release();
        }
    }
}
