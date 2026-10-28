using Karry.Modules.Identity.Infrastructure;
using Karry.Testing;

namespace Karry.Modules.Identity.Tests.Adapters;

/// <summary>The lock that serializes changes to the portal client between processes (API host, workers, replicas).</summary>
public sealed class KeycloakClientLockTests : IAsyncLifetime
{
    private PostgresServer postgres = null!;

    /// <summary>The application's own role, as the hosts connect (no superuser rights).</summary>
    private string app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        postgres = await PostgresServer.StartAsync();
        app = (await postgres.NewKarryDatabaseAsync()).App;
    }

    public async ValueTask DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task A_second_session_waits_until_the_first_releases_the_lock()
    {
        var first = new PostgresKeycloakClientLock(app);
        var second = new PostgresKeycloakClientLock(app);

        var held = await first.AcquireAsync(Ct);
        var waiting = second.AcquireAsync(Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        waiting.IsCompleted.Should().BeFalse("another session holds the lock");

        await held.DisposeAsync();
        var next = await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await next.DisposeAsync();
    }

    [Fact]
    public async Task A_cancelled_wait_gives_up_without_taking_the_lock()
    {
        var holder = new PostgresKeycloakClientLock(app);
        await using var held = await holder.AcquireAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(300));

        var wait = () => new PostgresKeycloakClientLock(app).AcquireAsync(timeout.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
    }
}
