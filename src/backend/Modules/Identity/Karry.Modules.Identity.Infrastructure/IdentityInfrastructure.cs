using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Tenancy.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Identity.Infrastructure;

public static class IdentityInfrastructure
{
    /// <summary>Users and invitations, the Keycloak adapter, messaging, the access-revocation check and role resolution.</summary>
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<IdentityDbContext>(connectionString);
        services.AddOutboxDispatcher<IdentityDbContext>(eventAssemblies: typeof(Domain.UserAccount).Assembly);
        services.Configure<IdentityOptions>(configuration.GetSection(IdentityOptions.Section));
        services.Configure<KeycloakOptions>(configuration.GetSection(KeycloakOptions.Section));
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.Section));

        services.AddScoped<IIdentityStore, IdentityStore>();
        services.AddScoped<BuildingBlocks.Entitlements.IUsageSource, OfficeSeats>();
        services.AddMemoryCache();
        services.AddScoped<RevocationCache>();
        services.AddScoped<IAccessRevocation>(sp => sp.GetRequiredService<RevocationCache>());
        services.AddScoped<IRevocationCache>(sp => sp.GetRequiredService<RevocationCache>());
        services.AddKarryAuthorization();
        services.AddSingleton<AccessCacheGenerations>();
        services.AddScoped<AccessResolver>();
        services.AddScoped<IAccessResolver>(sp => sp.GetRequiredService<AccessResolver>());
        services.AddScoped<IAccessCache>(sp => sp.GetRequiredService<AccessResolver>());
        services.AddScoped<IRoleStore, RoleStore>();
        services.AddScoped<RoleDirectory>();
        services.AddScoped<UserAdministration>();

        // One client and token cache for the process; connections are recycled so DNS changes are picked up.
        services.TryAddSingleton(sp => new KeycloakIdentityProvider(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(15) },
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeycloakOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IIdentityProvider>(sp => sp.GetRequiredService<KeycloakIdentityProvider>());
        services.TryAddSingleton<ISignInEventSource>(sp => sp.GetRequiredService<KeycloakIdentityProvider>());
        services.TryAddSingleton<IInvitationSender, InvitationSender>();
        services.AddScoped<IImportCursor, ImportCursor>();
        services.AddSingleton<ImportSignInFailures>();

        services.AddScoped<InviteUserHandler>();
        services.AddScoped<AcceptInvitationHandler>();
        services.AddScoped<CreateFieldUserHandler>();
        services.AddScoped<DeactivateUserHandler>();
        services.AddScoped<RoleAdministration>();
        services.AddScoped<MfaSync>();
        services.AddScoped<ChangeUserRolesHandler>();
        services.AddScoped<ResendInvitationHandler>();
        services.AddScoped<SetPinHandler>();
        services.AddScoped<DeviceStore>();
        services.AddScoped<IDeviceStore>(sp => sp.GetRequiredService<DeviceStore>());
        services.AddScoped<BuildingBlocks.Devices.IDeviceDirectory>(sp => sp.GetRequiredService<DeviceStore>());
        services.AddScoped<DeviceAdministration>();
        services.AddScoped<EnrollDeviceHandler>();
        services.AddScoped<TabletSession>();
        services.AddScoped<IImportTarget, UserImport>();
        services.AddScoped<IDomainEventSubscriber<Domain.UserInvited>, ImportedUserAccounts>();
        services.AddScoped<IFirstAdminInvitation, FirstAdminInvitation>();
        services.AddScoped<DemoUsersSeeder>();
        services.AddScoped<ITenantProvisionedHandler, KeycloakTenantSetup>();
        return services;
    }
}

/// <summary>A new tenant gets its Keycloak organization and its app addresses as redirect URIs (idempotent).</summary>
internal sealed class KeycloakTenantSetup(IIdentityProvider provider) : ITenantProvisionedHandler
{
    public Task HandleAsync(TenantSummary tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return provider.EnsureTenantAsync(tenant.Id, tenant.Slug, tenant.Name, cancellationToken);
    }
}
