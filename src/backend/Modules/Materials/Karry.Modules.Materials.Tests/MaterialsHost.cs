using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Storage;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Materials.Api;
using Karry.Modules.Tenancy.Api;
using Karry.SharedKernel.Authorization;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Materials.Tests;

/// <summary>
/// Tenancy, Materials, Trust and Imports (the Excel import of materials) behind the real pipeline, on PostgreSQL and the real roles. Every caller is a Tenant
/// Admin except <see cref="Operator"/>, who holds the operator role across the company.
/// </summary>
public sealed class MaterialsHost : IAsyncLifetime
{
    public const string Operator = "operator-1";

    private PostgresServer? server;
    private WebApplication? app;

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The imported files (P1.14).</summary>
    public InMemoryObjectStore Files { get; } = new();

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
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new MaterialsModule(), new Trust.Api.TrustModule(), new Imports.Api.ImportsModule()]);
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin, new Dictionary<string, string> { [Operator] = DefaultRoles.Operator });
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
