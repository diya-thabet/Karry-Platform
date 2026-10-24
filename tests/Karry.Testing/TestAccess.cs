using Karry.BuildingBlocks.Authorization;
using Karry.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Testing;

/// <summary>
/// For hosts under test without the Identity module: every caller holds one default role tenant-wide, with
/// the permissions the modules declare for it. Identity's own tests use real users instead.
/// </summary>
public static class TestAccess
{
    public static IServiceCollection TreatEveryoneAs(this IServiceCollection services, string role)
    {
        services.RemoveAll<IAccessResolver>();
        services.AddSingleton<IAccessResolver>(sp => new DefaultRoleResolver(sp.GetRequiredService<AuthorizationCatalog>(), role));
        return services;
    }

    private sealed class DefaultRoleResolver(AuthorizationCatalog catalog, string role) : IAccessResolver
    {
        public ValueTask<IReadOnlyList<AccessAssignment>> AssignmentsAsync(TenantId tenant, string subject, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AccessAssignment>>(
            [
                new(role, catalog.DefaultPermissionsOf(role),
                    catalog.SensitiveFields.ToDictionary(f => f.Code, f => f.DefaultFor(role), StringComparer.Ordinal)),
            ]);
    }
}
