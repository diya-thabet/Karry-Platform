using Karry.BuildingBlocks.Storage;
using Karry.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.IntegrationTests;

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

/// <summary>Company files in a real S3-compatible bucket (SeaweedFS, as the local stack; ADR-0026).</summary>
public sealed class ObjectStoreTests(SeaweedFs seaweed) : IClassFixture<SeaweedFs>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_object_is_put_read_back_with_its_type_and_deleted_in_a_bucket_created_on_first_use()
    {
        using var store = new S3ObjectStore(Options($"files-{Guid.NewGuid():N}"[..20]));
        var key = ObjectKeys.ForTenant(Guid.NewGuid(), "assets", "a/photo-1");
        byte[] content = [0xFF, 0xD8, 0xFF, 1, 2, 3];

        await store.PutAsync(key, content, "image/jpeg", Ct);
        var stored = await store.GetAsync(key, Ct);

        stored!.Content.Should().Equal(content);
        stored.ContentType.Should().Be("image/jpeg");
        await store.DeleteAsync(key, Ct);
        (await store.GetAsync(key, Ct)).Should().BeNull();
        await store.DeleteAsync(key, Ct);
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task A_stopped_storage_is_reported_unavailable_at_once()
    {
        // Hardening plan H-R4: nothing listens on port 1, so the connection is refused.
        using var store = new S3ObjectStore(new ObjectStoreOptions
        {
            ServiceUrl = "http://127.0.0.1:1",
            Bucket = "karry-files",
            AccessKey = SeaweedFs.AccessKey,
            SecretKey = SeaweedFs.SecretKey,
        });
        var key = ObjectKeys.ForTenant(Guid.NewGuid(), "assets", "a/photo-1");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await FluentActions.Awaiting(() => store.PutAsync(key, [1], "image/jpeg", Ct)).Should().ThrowAsync<ObjectStoreUnavailableException>();
        await FluentActions.Awaiting(() => store.GetAsync(key, Ct)).Should().ThrowAsync<ObjectStoreUnavailableException>();
        await FluentActions.Awaiting(() => store.DeleteAsync(key, Ct)).Should().ThrowAsync<ObjectStoreUnavailableException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    [Trait("Story", "P1.18a")]
    public async Task A_storage_that_accepts_connections_but_never_answers_is_given_up_at_the_call_deadline()
    {
        // A frozen server: the connection opens, no answer ever comes. The whole call ends at its deadline.
        using var frozen = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        frozen.Start();
        var port = ((System.Net.IPEndPoint)frozen.LocalEndpoint).Port;
        using var store = new S3ObjectStore(new ObjectStoreOptions
        {
            ServiceUrl = $"http://127.0.0.1:{port}",
            Bucket = "karry-files",
            AccessKey = SeaweedFs.AccessKey,
            SecretKey = SeaweedFs.SecretKey,
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await FluentActions.Awaiting(() => store.GetAsync(ObjectKeys.ForTenant(Guid.NewGuid(), "assets", "a/photo-1"), Ct))
            .Should().ThrowAsync<ObjectStoreUnavailableException>();
        // The deadline is ours, not the SDK's: on a slow CI runner the SDK's own retries took 31 s.
        watch.Elapsed.Should().BeLessThan(S3ObjectStore.CallBudget + TimeSpan.FromSeconds(3), "the SDK default would wait 100 s, four retries");
    }

    [Fact]
    public void The_store_follows_the_configuration_and_a_store_registered_first_wins()
    {
        var bucket = new ServiceCollection().AddObjectStore(Configuration(new() { ["Storage:Bucket"] = "karry-files", ["Storage:ServiceUrl"] = seaweed.ServiceUrl }));
        var none = new ServiceCollection().AddObjectStore(Configuration([]));
        var first = new ServiceCollection().AddSingleton<IObjectStore>(new InMemoryObjectStore());
        first.AddObjectStore(Configuration(new() { ["Storage:Bucket"] = "karry-files" }));

        bucket.BuildServiceProvider().GetRequiredService<IObjectStore>().Should().BeOfType<S3ObjectStore>();
        none.BuildServiceProvider().GetRequiredService<IObjectStore>().Available.Should().BeFalse("no bucket, no storage: the endpoints say so");
        first.BuildServiceProvider().GetRequiredService<IObjectStore>().Should().BeOfType<InMemoryObjectStore>();
    }

    private ObjectStoreOptions Options(string bucket) => new()
    {
        ServiceUrl = seaweed.ServiceUrl,
        Bucket = bucket,
        AccessKey = SeaweedFs.AccessKey,
        SecretKey = SeaweedFs.SecretKey,
        CreateBucketIfMissing = true,
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
