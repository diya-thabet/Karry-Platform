using Karry.BuildingBlocks.Entitlements;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Host.IntegrationTests;

/// <summary>
/// The API host enforces plans, limits and subscription states: their middleware lets everything through when no
/// resolver is registered (module test hosts), so the real host must have Billing's (ADR-0023).
/// </summary>
[Trait("Story", "US-P1-BIL-05")]
public sealed class EntitlementWiringTests
{
    [Fact]
    public async Task The_api_host_registers_the_entitlement_resolver_and_the_limit_guard()
    {
        // A database is configured but never reached: only the registrations are checked.
        await using var factory = new HostFactory(connectionString: "Host=127.0.0.1;Port=1;Database=karry;Username=karry_app;Password=unused");
        await using var scope = factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetService<IEntitlementResolver>().Should().NotBeNull();
        scope.ServiceProvider.GetService<ILimitGuard>().Should().NotBeNull();
        scope.ServiceProvider.GetServices<IUsageSource>().Select(s => s.Metric).Should().Contain(SharedKernel.Entitlements.Metrics.OfficeUsers);
    }
}
