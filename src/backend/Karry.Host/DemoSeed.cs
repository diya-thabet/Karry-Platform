using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Billing.Application;
using Karry.Modules.Identity.Application;
using Karry.Modules.Tenancy.Application;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Tenancy;

namespace Karry.Host;

/// <summary>
/// Demo data for local and demo environments: two tenants used by the isolation suite and demos, each with one user
/// per default role when Keycloak is configured (<c>Identity:Keycloak:BaseUrl</c>; password <c>Identity:DemoPassword</c>).
/// A demo tenant without a subscription (seeded before Billing existed) gets its trial here, the same way provisioning
/// starts it.
/// </summary>
internal static class DemoSeed
{
    public static readonly ProvisionTenant[] Tenants =
    [
        new("demo-carriere-a", "Carrière Démo A"),
        new("demo-carriere-b", "Carrière Démo B"),
    ];

    /// <summary>Local-only default (meets the realm's 12-character policy); never used outside seed-demo.</summary>
    public const string DemoPassword = "karry-demo-local-only";

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

            await using (var billing = services.CreateAsyncScope())
            {
                var id = new TenantId(result.Value.Id);
                billing.ServiceProvider.GetRequiredService<ITenantContext>().Set(id);
                await billing.ServiceProvider.GetServices<ITenantProvisionedHandler>().OfType<TrialOnProvisioning>().Single()
                    .HandleAsync(new TenantSummary(id, tenant.Slug, tenant.Name), cancellationToken).ConfigureAwait(false);
            }

            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            if (string.IsNullOrWhiteSpace(configuration["Identity:Keycloak:BaseUrl"]))
            {
                Console.WriteLine($"{tenant.Slug}: Identity:Keycloak:BaseUrl not set, no demo users");
                continue;
            }

            await using var users = services.CreateAsyncScope();
            var created = await users.ServiceProvider.GetRequiredService<DemoUsersSeeder>().SeedAsync(
                new TenantId(result.Value.Id), configuration["Identity:DemoPassword"] ?? DemoPassword, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"{tenant.Slug}: {created} demo user(s) created");
        }
    }
}
