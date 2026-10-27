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
