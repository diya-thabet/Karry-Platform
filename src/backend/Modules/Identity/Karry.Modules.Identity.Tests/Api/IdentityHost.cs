using System.Collections.Concurrent;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Persistence;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Api;
using Karry.Modules.Identity.Application;
using Karry.Modules.Tenancy.Api;
using Karry.Modules.Trust.Api;
using Karry.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

/// <summary>
/// Identity, Tenancy and Trust behind the real pipeline, on PostgreSQL with the real roles and a real Keycloak with
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

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public IServiceProvider Services => app!.Services;

    public async ValueTask InitializeAsync()
    {
        var keycloak = KeycloakServer.SharedAsync();
        postgres = await PostgresServer.StartAsync();
        Database = await postgres.NewKarryDatabaseAsync();
        Keycloak = await keycloak;

        var builder = WebApplication.CreateBuilder();
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
        builder.Services.AddModules(builder.Configuration, [new TenancyModule(), new IdentityModule(), new TrustModule()]);
        builder.Services.RemoveAll<IInvitationSender>();
        builder.Services.AddSingleton<IInvitationSender>(Sender);
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

        if (postgres is not null)
        {
            await postgres.DisposeAsync();
        }
    }
}
