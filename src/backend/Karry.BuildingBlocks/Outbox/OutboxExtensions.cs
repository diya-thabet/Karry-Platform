using System.Reflection;
using Karry.BuildingBlocks.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.BuildingBlocks.Outbox;

public static class OutboxExtensions
{
    /// <summary>
    /// Registers the dispatcher of <typeparamref name="TContext"/> and the event types of <paramref name="eventAssemblies"/>.
    /// A host runs several modules: the one serializer knows the event types of every module registered, whatever the
    /// order (otherwise a later module's events would be dead-lettered as unknown).
    /// </summary>
    public static IServiceCollection AddOutboxDispatcher<TContext>(
        this IServiceCollection services, OutboxOptions? options = null, params Assembly[] eventAssemblies)
        where TContext : ModuleDbContext
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new OutboxOptions());
        foreach (var assembly in eventAssemblies.Append(typeof(TContext).Assembly))
        {
            services.AddSingleton(new EventAssembly(assembly));
        }

        services.TryAddSingleton(sp => new EventSerializer(sp.GetServices<EventAssembly>().Select(e => e.Assembly)));
        services.AddMetrics();
        services.TryAddSingleton<OutboxMetrics>();
        services.AddSingleton<OutboxDispatcher<TContext>>();
        return services;
    }

    /// <summary>Also runs the dispatcher continuously as a hosted service (Worker host).</summary>
    public static IServiceCollection AddOutboxDispatcherService<TContext>(this IServiceCollection services)
        where TContext : ModuleDbContext =>
        services.AddHostedService<OutboxDispatcherService<TContext>>();
}

/// <summary>An assembly whose event types the <see cref="EventSerializer"/> knows (one registration per module and assembly).</summary>
internal sealed record EventAssembly(Assembly Assembly);
