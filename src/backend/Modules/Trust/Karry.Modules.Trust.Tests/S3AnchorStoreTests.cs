using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Karry.Modules.Trust.Domain;
using Karry.Modules.Trust.Infrastructure;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Trust.Tests;

/// <summary>SeaweedFS for this class's tests (<see cref="SeaweedFsServer"/>).</summary>
public sealed class SeaweedFs : IAsyncLifetime
{
    public const string AccessKey = SeaweedFsServer.AccessKey;
    public const string SecretKey = SeaweedFsServer.SecretKey;

    private SeaweedFsServer? server;

    public string ServiceUrl => server!.ServiceUrl;

    public async ValueTask InitializeAsync() => server = await SeaweedFsServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }
}

/// <summary>The S3 anchor store on a real Object Lock bucket (US-P1-AUD-02 AC1: compliance mode).</summary>
[Trait("Story", "US-P1-AUD-02")]
public sealed class S3AnchorStoreTests(SeaweedFs seaweed) : IClassFixture<SeaweedFs>
{
    private static readonly TenantId Tenant = new(Guid.Parse("01970000-0000-7000-8000-00000000000a"));

    // The real S3 server checks retain-until dates against its own clock: a fixed date here would turn into a date in the
    // past one day later and every put would be refused (P1.8 finding). Start at the real time, whole seconds.
    private readonly FakeTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    [Fact]
    public async Task An_anchor_is_stored_under_compliance_retention_and_cannot_be_deleted()
    {
        var (store, options, client) = Create();
        using (store)
        using (client)
        {
            var (anchor, document) = Anchor(5);
            await store.PutAsync(anchor, document, TestContext.Current.CancellationToken);

            var listed = await ListAsync(store);
            listed.Should().ContainSingle().Which.Should().Be(new Application.StoredAnchor(document, 1));

            var head = await client.GetObjectMetadataAsync(options.Bucket, anchor.ObjectKey, TestContext.Current.CancellationToken);
            head.ObjectLockMode.Should().Be(ObjectLockMode.Compliance);
            head.ObjectLockRetainUntilDate.Should().BeAfter(DateTime.UtcNow);
            var versions = await client.ListVersionsAsync(options.Bucket, anchor.ObjectKey, TestContext.Current.CancellationToken);
            var delete = () => client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = options.Bucket,
                Key = anchor.ObjectKey,
                VersionId = versions.Versions.Single().VersionId,
            }, TestContext.Current.CancellationToken);
            await delete.Should().ThrowAsync<AmazonS3Exception>("a compliance-locked version cannot be deleted by anyone");
        }
    }

    [Fact]
    public async Task A_second_write_to_an_anchor_key_is_seen_as_a_second_version_and_the_original_is_kept()
    {
        var (store, _, client) = Create();
        using (store)
        using (client)
        {
            var (anchor, original) = Anchor(7);
            await store.PutAsync(anchor, original, TestContext.Current.CancellationToken);
            await store.PutAsync(anchor, """{"forged":true}""", TestContext.Current.CancellationToken);

            var listed = (await ListAsync(store)).Should().ContainSingle().Subject;
            listed.Versions.Should().Be(2);
            listed.Document.Should().Be(original, "the oldest version is the evidence");
        }
    }

    private (S3AnchorStore Store, AnchoringOptions Options, AmazonS3Client Client) Create()
    {
        var options = new AnchoringOptions
        {
            ServiceUrl = seaweed.ServiceUrl,
            Bucket = $"anchors-{Guid.NewGuid():N}"[..30],
            AccessKey = SeaweedFs.AccessKey,
            SecretKey = SeaweedFs.SecretKey,
            RetentionDays = 1,
            CreateBucketIfMissing = true,
        };
        var client = new AmazonS3Client(new BasicAWSCredentials(SeaweedFs.AccessKey, SeaweedFs.SecretKey),
            new AmazonS3Config { ServiceURL = seaweed.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        return (new S3AnchorStore(options, clock), options, client);
    }

    private (LedgerAnchor Anchor, string Document) Anchor(long sequence)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = new SoftwareAnchorSigner(key);
        var anchor = new LedgerAnchor(Tenant, sequence, new string('e', 64), clock.GetUtcNow(), signer.KeyId);
        return (anchor, anchor.ToDocument(signer.Sign(anchor.SignedBytes())));
    }

    private static async Task<List<Application.StoredAnchor>> ListAsync(S3AnchorStore store)
    {
        var result = new List<Application.StoredAnchor>();
        await foreach (var stored in store.ListAsync(Tenant, TestContext.Current.CancellationToken))
        {
            result.Add(stored);
        }

        return result;
    }
}
