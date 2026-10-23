using Karry.Modules.Tenancy.Application;

namespace Karry.Host;

/// <summary>Demo data for local and demo environments: two tenants used by the isolation suite and demos.</summary>
internal static class DemoSeed
{
    public static readonly ProvisionTenant[] Tenants =
    [
        new("demo-carriere-a", "Carrière Démo A"),
        new("demo-carriere-b", "Carrière Démo B"),
    ];

    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        foreach (var tenant in Tenants)
        {
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ProvisionTenantHandler>()
                .HandleAsync(tenant, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Demo tenant {tenant.Slug}: {result.Error.Code}");
            }
        }
    }
}
