using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Testcontainers.PostgreSql;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// Hardening plan H-R1: PostgreSQL stops (or stops answering) and comes back. Readiness must say so quickly, so the
/// load balancer takes the instance out instead of queueing requests behind it, and the API must answer again on its
/// own, without a restart. Each test owns its server: these tests break it on purpose.
/// </summary>
[Trait("Story", "P1.18a")]
public sealed class DatabaseOutageTests : IAsyncLifetime
{
    /// <summary>How long a readiness probe may take before the load balancer counts it as failed (P1.17 sets 5 s).</summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(30);

    private PostgreSqlContainer database = null!;

    public async ValueTask InitializeAsync()
    {
        // A fixed host port, so the server comes back at the same address after a real stop and start.
        database = new PostgreSqlBuilder(PostgresFixture.Image).WithPortBinding(FreePort(), 5432).Build();
        await database.StartAsync();
    }

    public async ValueTask DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task A_stopped_database_makes_the_instance_unready_and_it_recovers_without_a_restart()
    {
        await using var factory = new HostFactory(database.GetConnectionString());
        using var client = factory.CreateClient();
        (await ReadyAsync(client)).Status.Should().Be(HttpStatusCode.OK);

        await database.StopAsync(TestContext.Current.CancellationToken);
        var down = await ReadyAsync(client);
        down.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
        down.Took.Should().BeLessThan(ProbeBudget);

        // A request that needs the database is told to come back, not handed an unexpected error.
        using var branding = await client.GetAsync(new Uri("/api/v1/tenancy/public/demo-carriere-a", UriKind.Relative), TestContext.Current.CancellationToken);
        branding.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        branding.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));

        await database.StartAsync(TestContext.Current.CancellationToken);
        (await RecoveryAsync(client)).Should().BeLessThan(RecoveryBudget, "the pool must drop its dead connections and reconnect on its own");
    }

    [Fact]
    public async Task A_database_that_stops_answering_fails_the_probe_within_its_budget()
    {
        await using var factory = new HostFactory(database.GetConnectionString());
        using var client = factory.CreateClient();
        (await ReadyAsync(client)).Status.Should().Be(HttpStatusCode.OK);

        // Paused: connections stay open but nothing answers, as in a network partition or a frozen server.
        await database.PauseAsync(TestContext.Current.CancellationToken);
        try
        {
            var hung = await ReadyAsync(client);
            hung.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            hung.Took.Should().BeLessThan(ProbeBudget, "a probe that hangs for the 30 s command timeout holds requests behind a dead instance");
        }
        finally
        {
            await database.UnpauseAsync(TestContext.Current.CancellationToken);
        }

        (await RecoveryAsync(client)).Should().BeLessThan(RecoveryBudget);
    }

    private static async Task<(HttpStatusCode Status, TimeSpan Took)> ReadyAsync(HttpClient client)
    {
        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        return (response.StatusCode, watch.Elapsed);
    }

    /// <summary>Time until readiness is green again, probing once a second as a load balancer would.</summary>
    private static async Task<TimeSpan> RecoveryAsync(HttpClient client)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < RecoveryBudget + ProbeBudget)
        {
            if ((await ReadyAsync(client)).Status == HttpStatusCode.OK)
            {
                return watch.Elapsed;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        return watch.Elapsed;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
