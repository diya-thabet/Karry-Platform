using Karry.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.BuildingBlocks.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers a module DbContext on PostgreSQL with Karry's conventions (snake_case, history table in the module
    /// schema) and records it for the migration runner. <paramref name="connectionString"/> is the runtime
    /// connection (<c>karry_app</c>), served by the process's one pool for it (<see cref="KarryDataSources"/>);
    /// migrations run separately as <c>karry_owner</c>. Tenant isolation is wired here:
    /// the scoped <see cref="ITenantContext"/> drives the RLS interceptor and the context's query filter.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string connectionString)
        where TContext : ModuleDbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.TryAddScoped<ITenantContext, TenantContext>();
        services.TryAddSingleton<KarryDataSources>();
        // The interceptor reads the scope's tenant: every command of a tenant's unit of work carries SET LOCAL.
        services.AddDbContext<TContext>((scope, options) =>
        {
            options.UseNpgsql(scope.GetRequiredService<KarryDataSources>().For(connectionString)).UseSnakeCaseNamingConvention();
            options.AddInterceptors(new TenantCommandInterceptor(scope.GetRequiredService<ITenantContext>()));
        });
        services.AddSingleton(new ModuleDatabase(typeof(TContext), ownerConnectionString =>
        {
            var options = new DbContextOptionsBuilder<TContext>();
            Configure(options, ownerConnectionString);
            return (TContext)Activator.CreateInstance(typeof(TContext), options.Options)!;
        }));
        return services;
    }

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}

/// <summary>A module database known to the host; <see cref="CreateForMigration"/> opens it with another role.</summary>
public sealed record ModuleDatabase(Type ContextType, Func<string, ModuleDbContext> CreateForMigration);
