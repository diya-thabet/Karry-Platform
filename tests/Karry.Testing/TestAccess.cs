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
    public static IServiceCollection TreatEveryoneAs(this IServiceCollection services, string role) =>
        services.TreatEveryoneAs(role, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// As above, except the token subjects in <paramref name="bySubject"/>, who hold their own default role, on the site
    /// <paramref name="siteOf"/> gives them (read at each request, so a test can add a site it just created).
    /// </summary>
    public static IServiceCollection TreatEveryoneAs(
        this IServiceCollection services, string role, IReadOnlyDictionary<string, string> bySubject, IReadOnlyDictionary<string, Guid>? siteOf = null)
    {
        services.RemoveAll<IAccessResolver>();
        services.AddSingleton<IAccessResolver>(sp =>
            new DefaultRoleResolver(sp.GetRequiredService<AuthorizationCatalog>(), role, bySubject, siteOf ?? new Dictionary<string, Guid>()));
        return services;
    }

    private sealed class DefaultRoleResolver(
        AuthorizationCatalog catalog, string role, IReadOnlyDictionary<string, string> bySubject, IReadOnlyDictionary<string, Guid> siteOf) : IAccessResolver
    {
        public ValueTask<IReadOnlyList<AccessAssignment>> AssignmentsAsync(TenantId tenant, string subject, CancellationToken cancellationToken)
        {
            var held = bySubject.GetValueOrDefault(subject, role);
            return ValueTask.FromResult<IReadOnlyList<AccessAssignment>>(
            [
                new(held, catalog.DefaultPermissionsOf(held),
                    catalog.SensitiveFields.ToDictionary(f => f.Code, f => f.DefaultFor(held), StringComparer.Ordinal),
                    siteOf.TryGetValue(subject, out var site) ? site : null),
            ]);
        }
    }
}
