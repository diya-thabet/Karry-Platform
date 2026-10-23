using System.Reflection;
using Karry.BuildingBlocks.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.BuildingBlocks.Outbox;

public static class OutboxExtensions
{
    /// <summary>Registers the dispatcher of <typeparamref name="TContext"/> and the event types of <paramref name="eventAssemblies"/>.</summary>
    public static IServiceCollection AddOutboxDispatcher<TContext>(
        this IServiceCollection services, OutboxOptions? options = null, params Assembly[] eventAssemblies)
        where TContext : ModuleDbContext
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new OutboxOptions());
        services.TryAddSingleton(_ => new EventSerializer(eventAssemblies.Append(typeof(TContext).Assembly)));
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
