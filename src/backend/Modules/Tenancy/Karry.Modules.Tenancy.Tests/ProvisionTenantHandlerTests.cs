using Karry.Modules.Tenancy.Application;
using Karry.Modules.Tenancy.Domain;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Tenancy.Tests;

/// <summary>
/// The race between "slug is free" and "insert" made deterministic (a parallel HTTP test may never interleave):
/// the repository reports the slug free, then the insert loses to a concurrent winner.
/// </summary>
[Trait("Story", "US-P1-TEN-01")]
[Trait("TestCase", "TC-P1-TEN-006")]
public sealed class ProvisionTenantHandlerTests
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Losing_the_race_to_an_identical_request_returns_the_winner()
    {
        var winner = Tenant.Provision(clock, "carriere-sfax", "Carrière de Sfax").Value;
        var handler = new ProvisionTenantHandler(new RacingRepository(winner), clock);

        var result = await handler.HandleAsync(new ProvisionTenant("carriere-sfax", "Carrière de Sfax"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(winner.Id.Value);
        result.Value.Created.Should().BeFalse();
    }

    [Fact]
    public async Task Losing_the_race_to_another_company_is_a_conflict()
    {
        var winner = Tenant.Provision(clock, "carriere-sfax", "Autre Société").Value;
        var handler = new ProvisionTenantHandler(new RacingRepository(winner), clock);

        var result = await handler.HandleAsync(new ProvisionTenant("carriere-sfax", "Carrière de Sfax"), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be("tenant.slug_taken");
    }

    /// <summary>First lookup: free. Insert: unique violation. Second lookup: the concurrent winner.</summary>
    private sealed class RacingRepository(Tenant winner) : ITenantRepository
    {
        private int lookups;

        public Task<Tenant?> FindBySlugAsync(TenantSlug slug, CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref lookups) == 1 ? null : winner);

        public Task<bool> TryAddAsync(Tenant tenant, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
