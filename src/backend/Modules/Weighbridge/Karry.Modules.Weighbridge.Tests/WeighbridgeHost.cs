using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Integrity.Api;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Tenancy.Api;
using Karry.Modules.Weighbridge.Api;
using Karry.SharedKernel.Authorization;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Weighbridge.Tests;

/// <summary>
/// Tenancy, Weighbridge, Integrity (review flags of unknown trucks) and Trust behind the real pipeline, on PostgreSQL and
/// the real roles. Every caller is a Tenant Admin except <see cref="Weighmaster"/> and <see cref="SiteManager"/>.
/// </summary>
public sealed class WeighbridgeHost : IAsyncLifetime
{
    public const string Weighmaster = "weighmaster-1";
    public const string SiteManager = "site-manager-1";

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
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new IntegrityModule(), new WeighbridgeModule(), new Trust.Api.TrustModule()]);

        // Ports of other modules Integrity asks (Identity, FieldOps, Notifications): nobody linked, nobody took part, notices dropped.
        builder.Services.AddSingleton<IReviewers, NoReviewers>();
        builder.Services.AddSingleton<IFlagParticipants, NoParticipants>();
        builder.Services.AddSingleton<Notifications.Contracts.INotices, NoNotices>();
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin, new Dictionary<string, string>
        {
            [Weighmaster] = DefaultRoles.Weighmaster,
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

internal sealed class NoReviewers : IReviewers
{
    public Task<Guid?> FieldAccountOfAsync(string subject, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
}

internal sealed class NoParticipants : IFlagParticipants
{
    public Task<bool> TookPartAsync(Guid person, IReadOnlyList<FlagSubject> related, CancellationToken cancellationToken) => Task.FromResult(false);
}

internal sealed class NoNotices : Notifications.Contracts.INotices
{
    public Task PublishAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, Notifications.Contracts.Notice notice, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
