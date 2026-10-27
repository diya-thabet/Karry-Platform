using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Karry.Testing;

/// <summary>
/// SeaweedFS, the same image as the local stack (ADR-0017), for the object storage tests (ledger anchors, company files).
/// Test projects wrap it in their own fixture.
/// </summary>
public sealed class SeaweedFsServer : IAsyncDisposable
{
    public const string AccessKey = "seaweed-test-access";
    public const string SecretKey = "seaweed-test-secret-only-for-tests";

    private readonly IContainer container;

    private SeaweedFsServer(IContainer container, string serviceUrl)
    {
        this.container = container;
        ServiceUrl = serviceUrl;
    }

    public string ServiceUrl { get; }

    public static async Task<SeaweedFsServer> StartAsync()
    {
        var config = $$"""{"identities":[{"name":"test","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],"actions":["Admin","Read","List","Tagging","Write"]}]}""";
        var container = new ContainerBuilder("chrislusf/seaweedfs:4.48")
            .WithCommand("server", "-dir=/data", "-master.volumeSizeLimitMB=256", "-volume.max=16", "-s3", "-s3.port=8333", "-s3.config=/etc/seaweedfs/s3.json")
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/seaweedfs/s3.json")
            .WithPortBinding(8333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8333).ForPath("/healthz")))
            .Build();
        await container.StartAsync().ConfigureAwait(false);
        return new SeaweedFsServer(container, $"http://{container.Hostname}:{container.GetMappedPublicPort(8333)}");
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
