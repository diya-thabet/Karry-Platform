using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Flags.Application;
using Karry.Modules.Flags.Domain;
using Karry.SharedKernel.Flags;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Flags.Tests;

/// <summary>
/// Hardening plan H-C1, deterministically: when another operator stored the same flag first, the change is a 409 (try
/// again) and the cache keeps what it has. The HTTP race test may or may not hit that moment; this one always does.
/// </summary>
[Trait("Story", "P1.18a")]
public sealed class FlagConflictTests
{
    private static readonly string Banner = FeatureFlags.PortalUsageBanner.Key;
    private readonly LosingStore store = new();
    private readonly CountingCache cache = new();

    private FlagOperations Operations => new(store, cache, new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero)), new ConfigurationBuilder().Build());

    [Fact]
    public async Task A_company_override_stored_first_by_another_operator_is_a_conflict()
    {
        var result = await Operations.SetTenantAsync(new TenantId(Guid.CreateVersion7()), Banner, new SetFlag(false, null), "operator-2", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(RecordVersions.Conflict);
        cache.Forgotten.Should().Be(0, "nothing changed, so nothing is forgotten");
    }

    [Fact]
    public async Task A_platform_value_stored_first_by_another_operator_is_a_conflict()
    {
        var result = await Operations.SetPlatformAsync(Banner, new SetFlag(false, null), "operator-2", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(RecordVersions.Conflict);
        cache.Forgotten.Should().Be(0);
    }

    /// <summary>A store where every save loses the race: the flag's first row was written by someone else.</summary>
    private sealed class LosingStore : IFlagStore
    {
        public Task<IReadOnlyList<PlatformFlag>> PlatformAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PlatformFlag>>([]);

        public Task<PlatformFlag?> FindPlatformAsync(string key, CancellationToken cancellationToken) => Task.FromResult<PlatformFlag?>(null);

        public void Add(PlatformFlag flag)
        {
        }

        public void Add(PlatformFlagChange change)
        {
        }

        public Task<IReadOnlyList<TenantFlag>> TenantAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TenantFlag>>([]);

        public Task<TenantFlag?> FindTenantAsync(string key, string subject, CancellationToken cancellationToken) => Task.FromResult<TenantFlag?>(null);

        public void Add(TenantFlag flag)
        {
        }

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class CountingCache : IFlagCache
    {
        public int Forgotten { get; private set; }

        public void Forget(TenantId? tenant) => Forgotten++;
    }
}
