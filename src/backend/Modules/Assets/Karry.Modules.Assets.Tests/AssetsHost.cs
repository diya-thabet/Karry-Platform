using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Storage;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Assets.Api;
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

namespace Karry.Modules.Assets.Tests;

/// <summary>Asset limits per tenant for the test host (Billing owns the real guard); a tenant not listed, and every other metric, is unlimited.</summary>
public sealed class AssetLimits
{
    public ConcurrentDictionary<Guid, long> ByTenant { get; } = new();
}

/// <summary>The soft-limit rule over the real usage sources, with asset limits from <see cref="AssetLimits"/> instead of a subscription.</summary>
internal sealed class AssetLimitGuard(IEnumerable<IUsageSource> sources, ITenantContext tenant, AssetLimits limits) : ILimitGuard
{
    public async Task<Error?> CheckCreateAsync(string metric, int count, CancellationToken cancellationToken)
    {
        if (metric != Metrics.Assets || !limits.ByTenant.TryGetValue(tenant.Current!.Value.Value, out var limit))
        {
            return null;
        }

        var usage = await sources.Single(s => s.Metric == metric).CountAsync(cancellationToken);
        return SoftLimit.Allows(usage, limit, count)
            ? null
            : Error.Forbidden("billing.limit_reached", "Limit reached.").With("metric", metric).With("limit", limit).With("usage", usage);
    }
}

/// <summary>
/// Tenancy, Organization (the sites), Assets, Trust and Imports (the Excel import of assets and meters) behind the real pipeline, on PostgreSQL and the real roles. Every
/// caller is a Tenant Admin except the subjects in <see cref="Roles"/>, who hold their role on the site in <see cref="SiteOf"/>.
/// </summary>
public sealed class AssetsHost : IAsyncLifetime
{
    public const string SiteManager = "site-manager-1";
    public const string Operator = "operator-1";
    public const string Executive = "executive-1";

    private PostgresServer? server;
    private WebApplication? app;

    public static IReadOnlyDictionary<string, string> Roles { get; } = new Dictionary<string, string>
    {
        [SiteManager] = DefaultRoles.SiteManager,
        [Operator] = DefaultRoles.Operator,
        [Executive] = DefaultRoles.Executive,
    };

    /// <summary>The site of a site role (a subject not listed holds its role across the company).</summary>
    public ConcurrentDictionary<string, Guid> SiteOf { get; } = new();

    public AssetLimits Limits { get; } = new();

    public InMemoryObjectStore Files { get; } = new();

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
        builder.Services.AddSingleton<IObjectStore>(Files);
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new OrganizationModule(), new AssetsModule(), new Trust.Api.TrustModule(), new Imports.Api.ImportsModule()]);
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin, Roles, SiteOf);
        builder.Services.AddSingleton(Limits);
        builder.Services.AddScoped<ILimitGuard, AssetLimitGuard>();
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
        app.UseIdempotency();
        app.MapModules();

        await MigrationRunner.MigrateAsync(app.Services, Database.Owner, CancellationToken.None);
        await app.StartAsync();
        Client = app.GetTestClient();
    }

    /// <summary>Runs the queued import steps (validation, commit), as the worker does.</summary>
    public Task RunImportsAsync() =>
        Services.GetRequiredService<Karry.BuildingBlocks.Outbox.OutboxDispatcher<Imports.Infrastructure.ImportsDbContext>>().DispatchPendingAsync(100, CancellationToken.None);

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
