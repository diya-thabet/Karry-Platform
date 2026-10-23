using Karry.BuildingBlocks.Ledger;
using Karry.BuildingBlocks.Persistence;
using Karry.Modules.Trust.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Trust.Infrastructure;

public static class TrustInfrastructure
{
    public static IServiceCollection AddTrustInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddModuleDbContext<TrustDbContext>(connectionString);
        services.AddSingleton<ILedgerWriter, PostgresLedgerWriter>();
        services.AddScoped<ILedgerReader, EfLedgerReader>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<VerifyLedgerHandler>();
        return services;
    }
}
