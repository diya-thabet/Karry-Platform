using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Storage;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Materials.Api;
using Karry.Modules.Organization.Api;
using Karry.Modules.Sales.Api;
using Karry.Modules.Tenancy.Api;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Sales.Tests;

/// <summary>Plan limits are Billing's concern, tested there: here sites can always be created (customers' allowed sites).</summary>
internal sealed class NoLimits : ILimitGuard
{
    public Task<Error?> CheckCreateAsync(string metric, int count, CancellationToken cancellationToken) => Task.FromResult<Error?>(null);
}

/// <summary>
/// Tenancy, Organization (sites), Materials (fractions sold as products), Sales, Trust and Imports (customer and product files) behind the real pipeline, on PostgreSQL and the real roles. Every caller
/// is a Tenant Admin except <see cref="Weighmaster"/>, <see cref="Finance"/> and <see cref="SiteManager"/>.
/// </summary>
public sealed class SalesHost : IAsyncLifetime
{
    public const string Weighmaster = "weighmaster-1";
    public const string Finance = "finance-1";
    public const string SiteManager = "site-manager-1";

    private PostgresServer? server;
    private WebApplication? app;

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The imported files (customers and products, P3.1).</summary>
    public InMemoryObjectStore Files { get; } = new();

    /// <summary>Runs the queued import steps (validation, commit), as the worker does.</summary>
    public Task RunImportsAsync() =>
        app!.Services.GetRequiredService<Karry.BuildingBlocks.Outbox.OutboxDispatcher<Imports.Infrastructure.ImportsDbContext>>()
            .DispatchPendingAsync(100, CancellationToken.None);

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
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new OrganizationModule(), new MaterialsModule(), new SalesModule(), new Trust.Api.TrustModule(), new Imports.Api.ImportsModule()]);
        builder.Services.AddScoped<ILimitGuard, NoLimits>();
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin, new Dictionary<string, string>
        {
            [Weighmaster] = DefaultRoles.Weighmaster,
            [Finance] = DefaultRoles.Finance,
            [SiteManager] = DefaultRoles.SiteManager,
        });
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
