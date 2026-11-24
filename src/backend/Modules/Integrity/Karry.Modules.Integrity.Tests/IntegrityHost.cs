using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Integrity.Api;
using Karry.Modules.Integrity.Contracts;
using Karry.Modules.Tenancy.Api;
using Karry.SharedKernel.Authorization;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Integrity.Tests;

/// <summary>The Integrity module over HTTP on real PostgreSQL, with tenants, roles and site scopes as in production.</summary>
public sealed class IntegrityHost : IAsyncLifetime
{
    public const string SiteManager = "site-manager-1";
    public const string Operator = "operator-1";

    private PostgresServer? server;
    private WebApplication? app;

    public static IReadOnlyDictionary<string, string> Roles { get; } = new Dictionary<string, string>
    {
        [SiteManager] = DefaultRoles.SiteManager,
        [Operator] = DefaultRoles.Operator,
    };

    /// <summary>The site of a site role (a subject not listed holds its role across the company).</summary>
    public ConcurrentDictionary<string, Guid> SiteOf { get; } = new();

    /// <summary>Office accounts linked to a field account (Identity's answer, ADR-0037 §4).</summary>
    public ConcurrentDictionary<string, Guid> FieldAccountOf { get; } = new();

    /// <summary>Who signed which entry (FieldOps' answer): entry root ID → field account.</summary>
    public ConcurrentDictionary<Guid, Guid> SignedBy { get; } = new();

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The host's services, for what a test runs outside a request (another module raising flags).</summary>
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
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new IntegrityModule(), new Trust.Api.TrustModule()]);
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin, Roles, SiteOf);
        builder.Services.AddSingleton<IReviewers>(new Linked(FieldAccountOf));
        builder.Services.AddSingleton<IFlagParticipants>(new Signers(SignedBy));
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

internal sealed class Linked(ConcurrentDictionary<string, Guid> links) : IReviewers
{
    public Task<Guid?> FieldAccountOfAsync(string subject, CancellationToken cancellationToken) =>
        Task.FromResult(links.TryGetValue(subject, out var field) ? field : (Guid?)null);
}

internal sealed class Signers(ConcurrentDictionary<Guid, Guid> signedBy) : IFlagParticipants
{
    public Task<bool> TookPartAsync(Guid person, IReadOnlyList<FlagSubject> related, CancellationToken cancellationToken) =>
        Task.FromResult(related.Any(r => signedBy.TryGetValue(r.Id, out var signer) && signer == person));
}
