using Karry.BuildingBlocks.Ledger;
using Karry.BuildingBlocks.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Trust.Infrastructure;

public static class TrustInfrastructure
{
    public static IServiceCollection AddTrustInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddModuleDbContext<TrustDbContext>(connectionString);
        services.AddSingleton<ILedgerWriter, PostgresLedgerWriter>();
        return services;
    }
}
