using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Organization.Api;
using Karry.Modules.Tenancy.Api;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Organization.Tests;

/// <summary>Sites bought per tenant for the test host (Billing owns the real guard); a tenant not listed is unlimited.</summary>
public sealed class SiteLimits
{
    public ConcurrentDictionary<Guid, long> ByTenant { get; } = new();
}

/// <summary>The soft-limit rule over Organization's real site meter, with limits from <see cref="SiteLimits"/> instead of a subscription.</summary>
internal sealed class SiteLimitGuard(IEnumerable<IUsageSource> sources, ITenantContext tenant, SiteLimits limits) : ILimitGuard
{
    public async Task<Error?> CheckCreateAsync(string metric, CancellationToken cancellationToken)
    {
        var usage = await sources.Single(s => s.Metric == metric).CountAsync(cancellationToken);
        long? limit = limits.ByTenant.TryGetValue(tenant.Current!.Value.Value, out var l) ? l : null;
        return SoftLimit.AllowsOneMore(usage, limit)
            ? null
            : Error.Forbidden("billing.limit_reached", "Limit reached.").With("metric", metric).With("limit", limit!.Value).With("usage", usage);
    }
}

/// <summary>
/// Tenancy, Organization and Trust behind the real pipeline, on PostgreSQL (with PostGIS) and the real roles. Every
/// caller is a Tenant Admin except the subjects in <see cref="Roles"/>, who hold the site in <see cref="SiteOf"/>.
/// </summary>
public sealed class OrganizationHost : IAsyncLifetime
{
    public const string SiteManager = "site-manager-1";
    public const string Operator = "operator-1";
    public const string Executive = "executive-1";

    /// <summary>Site manager of the site in <see cref="SiteOf"/> and, across the company, an executive (reads every site).</summary>
    public const string ManagerAndExecutive = "manager-executive-1";

    private PostgresServer? server;
    private WebApplication? app;

    public static IReadOnlyDictionary<string, string> Roles { get; } = new Dictionary<string, string>
    {
        [SiteManager] = DefaultRoles.SiteManager,
        [Operator] = DefaultRoles.Operator,
        [Executive] = DefaultRoles.Executive,
        [ManagerAndExecutive] = DefaultRoles.SiteManager,
    };

    public ConcurrentDictionary<string, Guid> SiteOf { get; } = new();

    public SiteLimits Limits { get; } = new();

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => app!.Services;

    public async ValueTask InitializeAsync()
    {
        server = await PostgresServer.StartAsync();
        Database = await server.NewKarryDatabaseAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Karry"] = Database.App });
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryTenancy(builder.Configuration);
        builder.Services.TrustTestIssuer();
        builder.Services.AddIdempotency(Database.App);
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new OrganizationModule(), new Trust.Api.TrustModule()]);
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin, Roles, SiteOf, new Dictionary<string, string> { [ManagerAndExecutive] = DefaultRoles.Executive });
        builder.Services.AddSingleton(Limits);
        builder.Services.AddScoped<ILimitGuard, SiteLimitGuard>();
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
        app.UseIdempotency();
        app.MapModules();

        await MigrationRunner.MigrateAsync(app.Services, Database.Owner, CancellationToken.None);
        await app.StartAsync();
        Client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }

        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }
}
