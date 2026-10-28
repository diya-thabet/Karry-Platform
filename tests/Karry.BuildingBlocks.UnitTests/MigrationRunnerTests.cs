using Karry.BuildingBlocks.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>Hosts of one process (test classes in parallel) never build the same context's model at the same time.</summary>
public sealed class MigrationRunnerTests
{
    private sealed class SameContext;

    private sealed class OtherContext;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A host whose only module database counts and holds its migrations, then fails (no real database needed).</summary>
    private static ServiceProvider Host(Type context, Func<ModuleDbContext> create) =>
        new ServiceCollection().AddLogging().AddSingleton(new ModuleDatabase(context, _ => create())).BuildServiceProvider();

    [Fact]
    public async Task Two_hosts_migrate_the_same_context_one_after_the_other_and_other_contexts_side_by_side()
    {
        using var release = new ManualResetEventSlim();
        var inside = 0;
        var most = 0;
        ModuleDbContext Hold()
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Decrement(ref inside);
            throw new InvalidOperationException("no database in this test");
        }

        await using var first = Host(typeof(SameContext), Hold);
        await using var second = Host(typeof(SameContext), Hold);
        var otherStarted = new TaskCompletionSource();
        await using var other = Host(typeof(OtherContext), () =>
        {
            otherStarted.SetResult();
            throw new InvalidOperationException("no database in this test");
        });

        var a = Task.Run(() => MigrationRunner.MigrateAsync(first, "Host=unused", Ct), Ct);
        while (Volatile.Read(ref inside) == 0)
        {
            await Task.Delay(10, Ct);
        }

        // The first host is inside; the others start now.
        var b = Task.Run(() => MigrationRunner.MigrateAsync(second, "Host=unused", Ct), Ct);
        var c = Task.Run(() => MigrationRunner.MigrateAsync(other, "Host=unused", Ct), Ct);
        await otherStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);

        Volatile.Read(ref inside).Should().Be(1, "the second host waits while the first builds the same model");
        release.Set();
        foreach (var run in new[] { a, b, c })
        {
            await run.Awaiting(t => t).Should().ThrowAsync<InvalidOperationException>();
        }

        most.Should().Be(1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
            {
                return;
            }
        }
    }
}
