using System.Net;
using System.Text.Json.Nodes;
using Karry.Modules.Sync.Application;
using Karry.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// Sequence gaps of tablets (ADR-0031 §4): waiting commands are measured, and a gap open for over 24 hours names its
/// tablet. Its own host, because the test moves the clock.
/// </summary>
[Trait("Story", "US-P2-OFF-03")]
public sealed class SyncGapTests(SyncHost host) : IClassFixture<SyncHost>
{
    [Fact]
    public async Task A_gap_open_for_over_a_day_names_its_tablet_and_closing_it_clears_the_measure()
    {
        using var tablet = await host.TabletAsync(host.A);
        await UploadAsync(tablet, [tablet.Command(2, new { n = 2 }), tablet.Command(3, new { n = 3 })]);

        var fresh = await MeasureAsync();
        host.Clock.Advance(MeasureSyncGaps.AlertAfter + TimeSpan.FromHours(1));
        var late = await MeasureAsync();
        await UploadAsync(tablet, [tablet.Command(1, new { n = 1 })]);
        var closed = await MeasureAsync();

        // Stored times keep microseconds, so ages are compared to within a millisecond.
        fresh.Should().BeEquivalentTo(new { Waiting = 2, Failed = 0 });
        fresh.OldestAge.Should().BeCloseTo(TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        fresh.Overdue.Should().BeEmpty();
        late.Overdue.Should().ContainSingle().Which.Should().BeEquivalentTo(new { DeviceId = tablet.Id, Waiting = 2 });
        late.OldestAge.Should().BeCloseTo(MeasureSyncGaps.AlertAfter + TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(1));
        closed.Should().BeEquivalentTo(new { Waiting = 0, OldestAge = (TimeSpan?)null, Failed = 0 });
        closed.Overdue.Should().BeEmpty();
    }

    private async Task<SyncGapRun> MeasureAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MeasureSyncGaps>().RunAsync(TestContext.Current.CancellationToken);
    }

    private static async Task UploadAsync(TestTablet tablet, JsonObject[] commands)
    {
        var response = await tablet.SendAsync(HttpMethod.Post, "/api/v1/sync/batches", new { commands });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
