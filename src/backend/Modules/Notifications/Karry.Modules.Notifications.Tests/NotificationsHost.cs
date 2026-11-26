using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Messaging;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Contracts;
using Karry.Modules.Notifications.Api;
using Karry.Modules.Organization.Contracts;
using Karry.Modules.Tenancy.Api;
using Karry.Modules.Verification.Contracts;
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

    /// <summary>The mail server the e-mails go to (ADR-0039 §4).</summary>
    public Mailbox Mailbox { get; } = new();

    /// <summary>Each company's sites and each site's review counts, as Organization and Verification would answer.</summary>
    public Sites Organization { get; } = new();

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
        builder.Services.AddSingleton<IEmailSender>(Mailbox);
        builder.Services.AddSingleton(Organization);
        builder.Services.AddScoped<ISiteDirectory>(sp => new CompanySites(Organization, sp.GetRequiredService<ITenantContext>()));
        builder.Services.AddSingleton<IReviewCounts>(Organization);
        builder.Services.AddSingleton<IAppAddresses>(new PortalAddresses());
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

    /// <summary>Each person's e-mail address and name, as Identity would answer for an active user.</summary>
    public ConcurrentDictionary<Guid, NoticeContact> Contacts { get; } = new();

    public Task<NoticeContact?> ContactOfAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Contacts.TryGetValue(userId, out var contact) ? contact : null);
}

/// <summary>
/// A mail server: it keeps what it was sent, and answers for each address the outcomes it was told to, then "sent"
/// (per address, so another test's e-mail never takes an outcome meant for this one).
/// </summary>
public sealed class Mailbox : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Received { get; } = new();

    public ConcurrentDictionary<string, ConcurrentQueue<EmailOutcome>> NextOutcomes { get; } = new();

    /// <summary>The next send to <paramref name="address"/> answers <paramref name="outcome"/>.</summary>
    public void Answer(string address, EmailOutcome outcome) => NextOutcomes.GetOrAdd(address, _ => new()).Enqueue(outcome);

    public Task<EmailOutcome> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var outcome = NextOutcomes.TryGetValue(message.To, out var planned) && planned.TryDequeue(out var next) ? next : EmailOutcome.Sent;
        if (outcome == EmailOutcome.Sent)
        {
            Received.Enqueue(message);
        }

        return Task.FromResult(outcome);
    }
}

/// <summary>Each company's sites (Organization) and each site's review counts (Verification).</summary>
public sealed class Sites : IReviewCounts
{
    public ConcurrentDictionary<Guid, List<SiteSummary>> OfCompany { get; } = new();

    public ConcurrentDictionary<Guid, ReviewCounts> Counts { get; } = new();

    public Task<ReviewCounts> OfSiteAsync(Guid siteId, DateTimeOffset since, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(Counts.GetValueOrDefault(siteId) ?? new ReviewCounts(0, 0, 0));
}

/// <summary>The current company's sites.</summary>
public sealed class CompanySites(Sites sites, ITenantContext tenant) : ISiteDirectory
{
    public Task<SiteSummary?> FindAsync(Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult(Mine().FirstOrDefault(s => s.Id == siteId));

    public Task<IReadOnlyList<SiteSummary>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SiteSummary>>(Mine());

    private List<SiteSummary> Mine() => tenant.Current is { } company ? sites.OfCompany.GetValueOrDefault(company.Value) ?? [] : [];
}

/// <summary>The portal of each company, as Identity's settings would give it.</summary>
public sealed class PortalAddresses : IAppAddresses
{
    public Uri PortalFor(string slug) => new($"https://{slug}.karry.test");
}
