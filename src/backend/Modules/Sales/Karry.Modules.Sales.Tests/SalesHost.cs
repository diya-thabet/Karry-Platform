using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
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
/// Tenancy, Organization (sites), Sales and Trust behind the real pipeline, on PostgreSQL and the real roles. Every caller
/// is a Tenant Admin except <see cref="Weighmaster"/> and <see cref="Finance"/>.
/// </summary>
public sealed class SalesHost : IAsyncLifetime
{
    public const string Weighmaster = "weighmaster-1";
    public const string Finance = "finance-1";

    private PostgresServer? server;
    private WebApplication? app;

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

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
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new OrganizationModule(), new SalesModule(), new Trust.Api.TrustModule()]);
        builder.Services.AddScoped<ILimitGuard, NoLimits>();
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin, new Dictionary<string, string>
        {
            [Weighmaster] = DefaultRoles.Weighmaster,
            [Finance] = DefaultRoles.Finance,
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
