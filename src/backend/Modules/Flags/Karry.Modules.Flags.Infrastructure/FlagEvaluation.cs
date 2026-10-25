using System.Collections.Concurrent;
using Karry.BuildingBlocks.Flags;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Flags.Application;
using Karry.Modules.Flags.Domain;
using Karry.SharedKernel.Flags;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Karry.Modules.Flags.Infrastructure;

/// <summary>
/// Evaluates flags from the cached platform values and the tenant's overrides (≤ 60 s, forgotten on change; ADR-0019).
/// Scoped: the tenant's overrides are read in the scope's tenant context (RLS).
/// </summary>
internal sealed class FlagEvaluator(IFlagStore store, FlagOperations operations, IMemoryCache cache, FlagCacheGenerations generations)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public async Task<(bool Value, FlagSource Source)> EvaluateAsync(FlagDefinition flag, TenantId? tenant, string? subject, CancellationToken cancellationToken)
    {
        var platform = await CachedAsync(("platform", (TenantId?)null, generations.Of(null)), async () =>
            (await store.PlatformAsync(cancellationToken).ConfigureAwait(false)).Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value!.Value, StringComparer.Ordinal))
            .ConfigureAwait(false);
        var overrides = tenant is null
            ? new Dictionary<(string, string), bool>()
            : await CachedAsync(("tenant", tenant, generations.Of(tenant)), async () =>
                (await store.TenantAsync(cancellationToken).ConfigureAwait(false)).Where(f => f.Value is not null).ToDictionary(f => (f.Key, f.Subject), f => f.Value!.Value))
                .ConfigureAwait(false);

        return FlagRules.Evaluate(flag, operations.EnvironmentValue(flag), Lookup(platform, flag.Key), Lookup(overrides, (flag.Key, string.Empty)),
            string.IsNullOrEmpty(subject) ? null : Lookup(overrides, (flag.Key, subject)));
    }

    private static bool? Lookup<TKey>(Dictionary<TKey, bool> values, TKey key)
        where TKey : notnull => values.TryGetValue(key, out var value) ? value : null;

    private async Task<T> CachedAsync<T>(object key, Func<Task<T>> load)
        where T : class
    {
        if (cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            return cached;
        }

        var loaded = await load().ConfigureAwait(false);
        cache.Set(key, loaded, Lifetime);
        return loaded;
    }
}

/// <summary>Generations in the cache keys: platform-wide (null) and per tenant. A platform change also moves every tenant's.</summary>
internal sealed class FlagCacheGenerations : IFlagCache
{
    private readonly ConcurrentDictionary<TenantId, long> tenants = new();
    private long platform;

    public long Of(TenantId? tenant) => tenant is { } id ? tenants.GetValueOrDefault(id) : Interlocked.Read(ref platform);

    public void Forget(TenantId? tenant)
    {
        if (tenant is { } id)
        {
            tenants.AddOrUpdate(id, 1, (_, generation) => generation + 1);
        }
        else
        {
            Interlocked.Increment(ref platform);
        }
    }
}

/// <summary>
/// Karry's OpenFeature provider (ADR-0019): boolean flags declared in <see cref="FeatureFlags"/>, evaluated per tenant
/// (<c>tenantId</c>), user (targeting key) and environment. Other types are a type mismatch; an unknown key is "flag not
/// found" and the caller's default applies.
/// </summary>
internal sealed class KarryFlagProvider(IServiceScopeFactory scopes) : FeatureProvider
{
    public const string Name = "karry-database";

    public override Metadata GetMetadata() => new(Name);

    public override async Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(string flagKey, bool defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (FeatureFlags.Find(flagKey) is not { } flag)
        {
            return new(flagKey, defaultValue, ErrorType.FlagNotFound, Reason.Error, errorMessage: $"Flag '{flagKey}' is not declared.");
        }

        TenantId? tenant = context is not null && context.TryGetValue(FeatureFlagClient.TenantAttribute, out var value) && Guid.TryParse(value?.AsString, out var id)
            ? new TenantId(id)
            : null;
        await using var scope = scopes.CreateAsyncScope();
        if (tenant is { } current)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(current);
        }

        var (enabled, source) = await scope.ServiceProvider.GetRequiredService<FlagEvaluator>()
            .EvaluateAsync(flag, tenant, context?.TargetingKey, cancellationToken).ConfigureAwait(false);
        var reason = source switch
        {
            FlagSource.User or FlagSource.Tenant => Reason.TargetingMatch,
            FlagSource.Default => Reason.Default,
            _ => Reason.Static,
        };
        return new(flagKey, enabled, ErrorType.None, reason, variant: source.ToString().ToLowerInvariant());
    }

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(string flagKey, string defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default) => Task.FromResult(Mismatch(flagKey, defaultValue));

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(string flagKey, int defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default) => Task.FromResult(Mismatch(flagKey, defaultValue));

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(string flagKey, double defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default) => Task.FromResult(Mismatch(flagKey, defaultValue));

    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(string flagKey, Value defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default) => Task.FromResult(Mismatch(flagKey, defaultValue));

    private static ResolutionDetails<T> Mismatch<T>(string key, T defaultValue) =>
        new(key, defaultValue, ErrorType.TypeMismatch, Reason.Error, errorMessage: "Karry flags are boolean.");
}

/// <summary>Sets this host's provider on its own OpenFeature domain at start-up, and removes it at shutdown.</summary>
internal sealed class FlagProviderRegistration(FeatureFlagDomain domain, KarryFlagProvider provider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Api.Instance.SetProviderAsync(domain.Name, provider, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
