using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Karry.Testing;

/// <summary>
/// OpenBao in dev mode with the transit engine mounted, for the server signing tests (ADR-0033 §3). Dev mode keeps keys
/// in memory, which is right for a test and wrong anywhere else. Same image as the technology stack's pin.
/// </summary>
public sealed class OpenBaoServer : IAsyncDisposable
{
    public const string Image = "openbao/openbao:2.7.2@sha256:5046911075e137e154ce49ab051502f4ec7cf367d484ae4a4dce665d9b90561f";
    public const string RootToken = "openbao-test-root-only-for-tests";

    private readonly IContainer container;

    private OpenBaoServer(IContainer container, string address)
    {
        this.container = container;
        Address = address;
    }

    public string Address { get; }

    public static async Task<OpenBaoServer> StartAsync()
    {
        var container = new ContainerBuilder(Image)
            .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200")
            .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", RootToken)
            .WithPortBinding(8200, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")))
            .Build();
        await container.StartAsync().ConfigureAwait(false);
        var server = new OpenBaoServer(container, $"http://{container.Hostname}:{container.GetMappedPublicPort(8200)}");
        await server.ExecAsync("bao", "secrets", "enable", "transit").ConfigureAwait(false);
        return server;
    }

    /// <summary>Runs the <c>bao</c> CLI inside the container as root (rotate a key, seal the server, …).</summary>
    public async Task<string> ExecAsync(params string[] command)
    {
        var result = await container.ExecAsync(["env", "BAO_ADDR=http://127.0.0.1:8200", $"BAO_TOKEN={RootToken}", .. command]).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result.Stdout
            : throw new InvalidOperationException($"{string.Join(' ', command)} failed: {result.Stderr}");
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
