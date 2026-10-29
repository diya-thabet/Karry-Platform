using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Entitlements;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Api;
using Karry.Modules.Identity.Application;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Api;
using Karry.Modules.Trust.Api;
using Karry.SharedKernel.Entitlements;
using Karry.SharedKernel.Results;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Karry.Modules.Identity.Tests.Api;

/// <summary>Captures invitations instead of sending them (the real sender is tested against Mailpit).</summary>
public sealed class CapturingSender : IInvitationSender
{
    public ConcurrentQueue<InvitationMessage> Sent { get; } = new();

    public Task<bool> SendAsync(InvitationMessage message, CancellationToken cancellationToken)
    {
        Sent.Enqueue(message);
        return Task.FromResult(true);
    }

    /// <summary>The token of the last invitation sent to <paramref name="destination"/> (it is only in the link).</summary>
    public string TokenFor(string destination) =>
        Uri.UnescapeDataString(Sent.Last(m => m.Destination == destination).Link.Fragment["#token=".Length..]);
}

/// <summary>Sites by code for the users import, as Organization's directory would answer (no tenant filter: codes are unique per test).</summary>
/// <summary>Keeps the break-glass notices instead of e-mailing them; every recipient counts as reached.</summary>
public sealed class CapturingSupportNotifier : ISupportNotifier
{
    public ConcurrentQueue<BreakGlassNotice> Sent { get; } = new();

    public Task<int> BreakGlassAsync(BreakGlassNotice notice, CancellationToken cancellationToken)
    {
        Sent.Enqueue(notice);
        return Task.FromResult(notice.Recipients.Count);
    }
}

/// <summary>Keeps the event IDs the host logs at warning or above, to check security-relevant log events.</summary>
public sealed class CapturingLogs : ILoggerProvider
{
    public ConcurrentQueue<(int EventId, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLogs logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                logs.Entries.Enqueue((eventId.Id, formatter(state, exception)));
            }
        }
    }
}

public sealed class TestSites : ISiteDirectory
{
    public ConcurrentDictionary<Guid, SiteSummary> All { get; } = new();

    public Guid Add(string code)
    {
        var id = Guid.CreateVersion7();
        All[id] = new SiteSummary(id, code, $"Carrière {code}", "Africa/Tunis", false);
        return id;
    }

    public Task<SiteSummary?> FindAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(All.GetValueOrDefault(siteId));

    public Task<IReadOnlyList<SiteSummary>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SiteSummary>>([.. All.Values]);
}

/// <summary>Office-seat limits per tenant for the test host (Billing owns the real guard); a tenant not listed is unlimited.</summary>
public sealed class SeatLimits
{
    public ConcurrentDictionary<Guid, long> ByTenant { get; } = new();
}

/// <summary>
/// The soft-limit rule over Identity's real seat meter, with limits from <see cref="SeatLimits"/> instead of a plan:
/// proves the invitation calls the guard and the meter counts the right users.
/// </summary>
internal sealed class SeatLimitGuard(IEnumerable<IUsageSource> sources, ITenantContext tenant, SeatLimits limits) : ILimitGuard
{
    public async Task<Error?> CheckCreateAsync(string metric, int count, CancellationToken cancellationToken)
    {
        var usage = await sources.Single(s => s.Metric == metric).CountAsync(cancellationToken);
        long? limit = limits.ByTenant.TryGetValue(tenant.Current!.Value.Value, out var l) ? l : null;
        return SoftLimit.Allows(usage, limit, count)
            ? null
            : Error.Forbidden("billing.limit_reached", "Limit reached.").With("metric", metric).With("limit", limit!.Value).With("usage", usage);
    }
}

/// <summary>
/// Identity, Tenancy, Trust and Imports (the users import, with sites from <see cref="Sites"/>) behind the real pipeline, on PostgreSQL with the real roles and a real Keycloak with
/// the repository realm. Tokens for the API come from the test issuer (the API side of Keycloak tokens is proven by
/// <c>ApiTokenValidationTests</c>); Keycloak is the account store the module manages.
/// </summary>
public sealed class IdentityHost : IAsyncLifetime
{
    private PostgresServer? postgres;
    private WebApplication? app;

    public KeycloakServer Keycloak { get; private set; } = null!;

    public KarryDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public CapturingSender Sender { get; } = new();

    public CapturingSupportNotifier SupportNotices { get; } = new();

    public CapturingLogs Logs { get; } = new();

    public SeatLimits SeatLimits { get; } = new();

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>The sites the users import names (Organization's directory, without its module and permissions).</summary>
    public TestSites Sites { get; } = new();

    /// <summary>The imported files (P1.14).</summary>
    public InMemoryObjectStore Files { get; } = new();

    public IServiceProvider Services => app!.Services;

    public async ValueTask InitializeAsync()
    {
        var keycloak = KeycloakServer.SharedAsync();
        postgres = await PostgresServer.StartAsync();
        Database = await postgres.NewKarryDatabaseAsync();
        Keycloak = await keycloak;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.AddProvider(Logs);
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Karry"] = Database.App,
            ["Identity:PortalOrigin"] = "https://{slug}.karry.test",
            ["Identity:FieldOrigin"] = "https://{slug}.field.karry.test",
            ["Identity:Keycloak:BaseUrl"] = Keycloak.BaseUrl.ToString(),
            ["Identity:Keycloak:ClientSecret"] = KeycloakServer.IdentityClientSecret,
        });
        builder.Services.AddSingleton<TimeProvider>(Clock);
        builder.Services.AddKarryApiConventions();
        builder.Services.AddKarryTenancy(builder.Configuration);
        builder.Services.TrustTestIssuer();
        builder.Services.AddIdempotency(Database.App);
        builder.Services.AddSingleton<Karry.BuildingBlocks.Storage.IObjectStore>(Files);
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new IdentityModule(), new TrustModule(), new ProbeModule(), new Imports.Api.ImportsModule()]);
        builder.Services.RemoveAll<IInvitationSender>();
        builder.Services.AddSingleton<IInvitationSender>(Sender);
        builder.Services.RemoveAll<ISupportNotifier>();
        builder.Services.AddSingleton<ISupportNotifier>(SupportNotices);
        builder.Services.AddSingleton(SeatLimits);
        builder.Services.AddSingleton<ISiteDirectory>(Sites);
        builder.Services.AddScoped<ILimitGuard, SeatLimitGuard>();
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

    /// <summary>Delivers Identity's events, as the worker does (imported users get their Keycloak accounts and invitations).</summary>
    public Task RunIdentityEventsAsync() =>
        Services.GetRequiredService<Karry.BuildingBlocks.Outbox.OutboxDispatcher<Infrastructure.IdentityDbContext>>().DispatchPendingAsync(100, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }

        if (postgres is not null)
        {
            await postgres.DisposeAsync();
        }
    }
}
