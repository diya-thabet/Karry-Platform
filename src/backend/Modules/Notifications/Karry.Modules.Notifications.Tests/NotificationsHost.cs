using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Notifications.Api;
using Karry.Modules.Tenancy.Api;
using Karry.SharedKernel.Authorization;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Notifications.Tests;

/// <summary>The Notifications module over HTTP on real PostgreSQL; who holds which role is Identity's answer, faked here.</summary>
public sealed class NotificationsHost : IAsyncLifetime
{
    private PostgresServer? server;
    private WebApplication? app;

    /// <summary>Who holds a role on a site (null: across the company), as Identity would answer.</summary>
    public People People { get; } = new();

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
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new NotificationsModule(), new Trust.Api.TrustModule()]);
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin);
        builder.Services.AddSingleton<INoticeAudiences>(People);
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseKarryTenancy();
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

/// <summary>A fake Identity: role holders per site and company, and the user behind each signed-in account.</summary>
public sealed class People : INoticeAudiences
{
    public ConcurrentDictionary<(string Role, Guid? Site), List<Guid>> Holders { get; } = new();

    public ConcurrentDictionary<string, Guid> UserOf { get; } = new();

    public Task<IReadOnlyList<Guid>> HoldersAsync(IReadOnlyCollection<string> roles, Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. roles.SelectMany(role => Holders.GetValueOrDefault((role, siteId)) ?? []).Distinct()]);

    public Task<Guid?> UserIdOfAsync(string subject, CancellationToken cancellationToken) =>
        Task.FromResult(UserOf.TryGetValue(subject, out var user) ? user : (Guid?)null);
}
