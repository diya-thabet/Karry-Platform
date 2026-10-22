using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.Persistence;

public static class PersistenceExtensions
{
    /// <summary>Registers a module DbContext on PostgreSQL with Karry's conventions (snake_case, split queries off, retries off).</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string connectionString)
        where TContext : ModuleDbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<TContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        return services;
    }
}
