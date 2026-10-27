using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Imports;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Storage;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Imports.Api;
using Karry.Modules.Imports.Application;
using Karry.Modules.Imports.Infrastructure;
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
/// and a permission the Tenant Admin holds. Committed probes live in <see cref="ImportsHost.Probes"/> per tenant (code →
/// name), keyed by code like a real target: same name unchanged, other name updated, new code created. A code in
/// <see cref="ImportsHost.Refused"/> is an error, so a test can change the data between the preview and the commit.
/// </summary>
public class ProbeTarget(ImportsHost host, ITenantContext tenant) : IImportTarget
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

    public Task<ImportValidation> ValidateAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken) => Task.FromResult(Plan(rows));

    public Task<ImportCommit> CommitAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        var validation = Plan(rows);
        if (validation.HasErrors)
        {
            return Task.FromResult(new ImportCommit(validation, 0, 0, 0));
        }

        var probes = Stored;
        foreach (var row in rows)
        {
            probes[row["code"]!] = row["name"]!;
        }

        return Task.FromResult(new ImportCommit(
            validation, Count(validation, ImportAction.Create), Count(validation, ImportAction.Update), Count(validation, ImportAction.Unchanged)));
    }

    private ConcurrentDictionary<string, string> Stored => host.Probes.GetOrAdd(tenant.Current!.Value.Value, _ => new(StringComparer.Ordinal));

    private static int Count(ImportValidation validation, ImportAction action) => validation.Plans.Count(p => p.Action == action);

    private ImportValidation Plan(IReadOnlyList<ImportRow> rows)
    {
        var issues = new List<ImportIssue>();
        var plans = new List<ImportRowPlan>();
        var counts = rows.Where(r => r["code"] is not null).GroupBy(r => r["code"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var before = issues.Count;
            var code = row["code"];
            if (code is null)
            {
                issues.Add(ImportIssue.Error(row, "code", "imports.value_required"));
            }
            else if (counts[code] > 1)
            {
                issues.Add(ImportIssue.Error(row, "code", "probes.code_duplicate", new Dictionary<string, string> { ["code"] = code }));
            }
            else if (host.Refused.ContainsKey(code))
            {
                issues.Add(ImportIssue.Error(row, "code", "probes.code_refused"));
            }

            if (row["name"] is null)
            {
                issues.Add(ImportIssue.Error(row, "name", "imports.value_required"));
            }

            if (issues.Count == before)
            {
                var action = !Stored.TryGetValue(code!, out var name) ? ImportAction.Create : name == row["name"] ? ImportAction.Unchanged : ImportAction.Update;
                plans.Add(new ImportRowPlan(row.Number, code!, action));
            }
        }

        return new ImportValidation(issues, plans);
    }
}

/// <summary>A kind whose data nobody may change: its template is refused even to a Tenant Admin.</summary>
public sealed class LockedTarget(ImportsHost host, ITenantContext tenant) : ProbeTarget(host, tenant)
{
    public override string Kind => "locked";

    public override string Permission => "imports.test.nobody";
}

/// <summary>A scanner that refuses the files whose SHA-256 is in <see cref="ImportsHost.Infected"/>.</summary>
public sealed class TestScanner(ImportsHost host) : IFileScanner
{
    public string Name => "test-scanner";

    public Task<bool> IsCleanAsync(byte[] file, CancellationToken cancellationToken) =>
        Task.FromResult(!host.Infected.ContainsKey(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file))));
}

/// <summary>
/// Tenancy, Imports and Trust (the ledger) behind the real pipeline, on PostgreSQL and the real roles, with the <see cref="ProbeTarget"/>
/// kind, files in memory and the <see cref="TestScanner"/>. Every caller is a Tenant Admin except the subjects in
/// <see cref="Roles"/> (read at each request and at each job run, so a test can take rights away). Jobs run when a
/// test calls <see cref="RunJobsAsync"/> (the worker's outbox dispatcher).
/// </summary>
public sealed class ImportsHost : IAsyncLifetime
{
    public const string Operator = "operator-1";

    private PostgresServer? server;
    private WebApplication? app;

    public ConcurrentDictionary<string, string> Roles { get; } = new(StringComparer.Ordinal) { [Operator] = DefaultRoles.Operator };

    /// <summary>Committed probes: tenant → code → name.</summary>
    public ConcurrentDictionary<Guid, ConcurrentDictionary<string, string>> Probes { get; } = new();

    public ConcurrentDictionary<string, bool> Refused { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, bool> Infected { get; } = new(StringComparer.Ordinal);

    public InMemoryObjectStore Files { get; } = new();

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
        builder.Services.AddSingleton<IObjectStore>(Files);
        builder.Services.AddSingleton<IFileScanner, TestScanner>();
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

    /// <summary>Runs the queued import steps, as the worker does.</summary>
    public Task RunJobsAsync() => Services.GetRequiredService<OutboxDispatcher<ImportsDbContext>>().DispatchPendingAsync(100, CancellationToken.None);

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
