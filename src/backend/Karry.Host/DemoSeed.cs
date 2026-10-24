using Karry.Modules.Identity.Application;
using Karry.Modules.Tenancy.Application;
using Karry.SharedKernel.Tenancy;

namespace Karry.Host;

/// <summary>
/// Demo data for local and demo environments: two tenants used by the isolation suite and demos, each with one user
/// per default role when Keycloak is configured (<c>Identity:Keycloak:BaseUrl</c>; password <c>Identity:DemoPassword</c>).
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
