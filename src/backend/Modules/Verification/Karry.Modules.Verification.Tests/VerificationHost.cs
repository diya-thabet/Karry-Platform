using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Tenancy.Api;
using Karry.Modules.Verification.Api;
using Karry.Modules.Verification.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Verification.Tests;

/// <summary>The Verification module over HTTP on real PostgreSQL, with tenants, roles and site scopes as in production.</summary>
public sealed class VerificationHost : IAsyncLifetime
{
    public const string Controller = "controller-1";
    public const string Operator = "operator-1";

    private PostgresServer? server;
    private WebApplication? app;

    public static IReadOnlyDictionary<string, string> Roles { get; } = new Dictionary<string, string>
    {
        [Controller] = DefaultRoles.Controller,
        [Operator] = DefaultRoles.Operator,
    };

    /// <summary>The site of a site role (a subject not listed holds its role across the company).</summary>
    public ConcurrentDictionary<string, Guid> SiteOf { get; } = new();

    /// <summary>Identity's office ↔ field account links, by subject (ADR-0037 §4).</summary>
    public LinkedAccounts People { get; } = new();

    /// <summary>FieldOps' answer to "did this person drive this machine then?", as (person, asset) pairs.</summary>
    public OperatedAssets Operated { get; } = new();

    /// <summary>Who holds the escalation role on a site, as Identity answers the Notifications module.</summary>
    public RoleHolders Audiences { get; } = new();

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The host's services, for what a test runs outside a request (another module submitting a record version).</summary>
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
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new VerificationModule(), new Trust.Api.TrustModule(), new Notifications.Api.NotificationsModule()]);
        builder.Services.TreatEveryoneAs(DefaultRoles.TenantAdmin, Roles, SiteOf);
        builder.Services.AddSingleton<IReviewers>(People);
        builder.Services.AddSingleton<IReviewableRecords>(Operated);
        builder.Services.AddSingleton<INoticeAudiences>(Audiences);
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

public sealed class LinkedAccounts : IReviewers
{
    public ConcurrentDictionary<string, Guid> FieldAccountOf { get; } = new();

    public Task<Guid?> FieldAccountOfAsync(string subject, CancellationToken cancellationToken) =>
        Task.FromResult(FieldAccountOf.TryGetValue(subject, out var field) ? field : (Guid?)null);
}

public sealed class OperatedAssets : IReviewableRecords
{
    public ConcurrentDictionary<(Guid Person, Guid Asset), bool> Drove { get; } = new();

    public string RecordType => ReviewRecordTypes.ShiftEntry;

    public Task<bool> OperatedAsync(Guid person, Guid assetId, DateTimeOffset from, DateTimeOffset? to, CancellationToken cancellationToken) =>
        Task.FromResult(Drove.ContainsKey((person, assetId)));

    /// <summary>What FieldOps answers to the next correction (default: version 3, one meter value changed, within the thresholds).</summary>
    public CorrectionOutcome NextCorrection { get; set; } = Within;

    public static CorrectionOutcome Within { get; } =
        new CorrectionOutcome.Corrected(3, "sha256:" + new string('e', 64), [new FieldChange("endValue", "108.0", "107.5")], Material: false);

    /// <summary>The corrections asked of FieldOps: record, version, fields.</summary>
    public ConcurrentQueue<(Guid RecordId, int Version, IReadOnlyDictionary<string, string> Fields)> Corrections { get; } = new();

    public Task<CorrectionOutcome> CorrectAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, Guid recordId, int version, IReadOnlyDictionary<string, string> fields,
        string reason, string? actor, CorrectionThresholds thresholds, CancellationToken cancellationToken)
    {
        Corrections.Enqueue((recordId, version, fields));
        return Task.FromResult(NextCorrection);
    }
}

public sealed class RoleHolders : INoticeAudiences
{
    public ConcurrentDictionary<(string Role, Guid? Site), List<Guid>> Holders { get; } = new();

    public Task<IReadOnlyList<Guid>> HoldersAsync(IReadOnlyCollection<string> roles, Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. roles.SelectMany(role => Holders.GetValueOrDefault((role, siteId)) ?? [])]);

    public Task<Guid?> UserIdOfAsync(string subject, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<NoticeContact?> ContactOfAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<NoticeContact?>(null);
}
