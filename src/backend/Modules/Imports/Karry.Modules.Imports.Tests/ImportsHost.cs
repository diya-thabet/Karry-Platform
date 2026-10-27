using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Imports.Api;
using Karry.Modules.Tenancy.Api;
using Karry.SharedKernel.Authorization;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Imports.Tests;

/// <summary>
/// An import kind for the tests (<c>probes</c>): code and name, a drop-down of the values in <see cref="ImportsHost.ProbeList"/>,
/// and a permission the Tenant Admin holds. Validation and commit come in later slices.
/// </summary>
public class ProbeTarget(ImportsHost host) : IImportTarget
{
    public virtual string Kind => "probes";

    public virtual string Permission => "tenancy.settings.manage";

    public IReadOnlyList<ImportColumn> Columns { get; } =
    [
        new("code", "Code", "Code", Required: true),
        new("name", "Name", "Nom", Required: true, Example: "Sonde"),
        new("site", "Site", "Site", Required: false, List: "sites"),
    ];

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>> { ["sites"] = host.ProbeList });

    public Task<ImportValidation> ValidateAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ImportCommit> CommitAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>A kind whose data nobody may change: its template is refused even to a Tenant Admin.</summary>
public sealed class LockedTarget(ImportsHost host) : ProbeTarget(host)
{
    public override string Kind => "locked";

    public override string Permission => "imports.test.nobody";
}

/// <summary>
/// Tenancy, Imports and Trust (the ledger) behind the real pipeline, on PostgreSQL and the real roles, with the <see cref="ProbeTarget"/>
/// kind. Every caller is a Tenant Admin except the subjects in <see cref="Roles"/>.
/// </summary>
public sealed class ImportsHost : IAsyncLifetime
{
    public const string Operator = "operator-1";

    private PostgresServer? server;
    private WebApplication? app;

    public static IReadOnlyDictionary<string, string> Roles { get; } = new Dictionary<string, string> { [Operator] = DefaultRoles.Operator };

    public IReadOnlyList<string> ProbeList { get; } = ["CAR-NORD", "=EVIL()"];

    public ConcurrentDictionary<string, Guid> SiteOf { get; } = new();

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
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new ImportsModule(), new Trust.Api.TrustModule()]);
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin, Roles, SiteOf);
        builder.Services.AddSingleton(this);
        builder.Services.AddScoped<IImportTarget, ProbeTarget>();
        builder.Services.AddScoped<IImportTarget, LockedTarget>();
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
