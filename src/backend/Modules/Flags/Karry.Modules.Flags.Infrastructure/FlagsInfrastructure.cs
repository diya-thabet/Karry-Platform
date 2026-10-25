using Karry.BuildingBlocks.Flags;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Flags.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Flags.Infrastructure;

public static class FlagsInfrastructure
{
    /// <summary>Flag storage, the cached evaluator, Karry's OpenFeature provider on this host's domain, and the call-site client.</summary>
    public static IServiceCollection AddFlagsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<FlagsDbContext>(connectionString);
        services.AddOutboxDispatcher<FlagsDbContext>(eventAssemblies: typeof(Domain.TenantFlag).Assembly);
        services.AddScoped<IFlagStore, FlagStore>();
        services.AddScoped<FlagOperations>();
        services.AddMemoryCache();
        services.AddSingleton<FlagCacheGenerations>();
        services.AddSingleton<IFlagCache>(sp => sp.GetRequiredService<FlagCacheGenerations>());
        services.AddScoped<FlagEvaluator>();
        services.AddSingleton<KarryFlagProvider>();
        services.TryAddSingleton(new FeatureFlagDomain($"karry-{Guid.NewGuid():N}"));
        services.AddHostedService<FlagProviderRegistration>();
        services.AddHttpContextAccessor();
        services.AddScoped<FeatureFlagClient>();
        return services;
    }
}
