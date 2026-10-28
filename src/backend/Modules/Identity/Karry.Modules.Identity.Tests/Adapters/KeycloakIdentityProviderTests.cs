using System.Net.Http.Json;
using System.Text.Json;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Infrastructure;
using Karry.Modules.Identity.Tests.Realm;
using Karry.SharedKernel.Tenancy;
using Karry.Testing;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Identity.Tests.Adapters;

/// <summary>The Keycloak admin adapter against the repository realm: every operation is idempotent, as the handlers assume.</summary>
[Trait("Story", "US-P1-IAM-03")]
public sealed class KeycloakIdentityProviderTests(KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>, IDisposable
{
    private readonly KeycloakIdentityProvider provider = new(
        new HttpClient(),
        Options.Create(new KeycloakOptions { BaseUrl = keycloak.Server.BaseUrl, ClientSecret = KeycloakServer.IdentityClientSecret }),
        Options.Create(new IdentityOptions { PortalOrigin = "https://{slug}.karry.test", FieldOrigin = "https://{slug}.field.karry.test" }),
        TimeProvider.System);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => provider.Dispose();

    [Fact]
    [Trait("Story", "US-P1-TEN-01")]
    public async Task Tenant_setup_creates_one_organization_and_registers_the_portal_address_once()
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        var slug = $"kc-{Guid.NewGuid():N}"[..16];

        await provider.EnsureTenantAsync(tenant, slug, "Carrière Keycloak", Ct);
        await provider.EnsureTenantAsync(tenant, slug, "Carrière Keycloak", Ct);

        var organizations = await JsonAsync($"organizations?q=tenant_id:{tenant}");
        organizations.GetArrayLength().Should().Be(1);
        organizations[0].GetProperty("alias").GetString().Should().Be(slug);
        organizations[0].GetProperty("name").GetString().Should().Be($"Carrière Keycloak ({slug})", "organization names are unique per realm");
        var portal = (await JsonAsync("clients?clientId=karry-portal"))[0];
        Strings(portal, "redirectUris").Count(r => r == $"https://{slug}.karry.test/*").Should().Be(1);
        Strings(portal, "webOrigins").Should().Contain($"https://{slug}.karry.test");
        portal.GetProperty("attributes").GetProperty("post.logout.redirect.uris").GetString().Should().Contain($"https://{slug}.karry.test/*");
        Strings(portal, "redirectUris").Should().Contain("http://localhost:5173/*", "existing addresses are kept");
    }

    [Fact]
    public async Task Concurrent_tenant_setups_lose_no_redirect_uri()
    {
        var slugs = Enumerable.Range(0, 4).Select(_ => $"kc-{Guid.NewGuid():N}"[..16]).ToList();

        await Task.WhenAll(slugs.Select(slug => provider.EnsureTenantAsync(new TenantId(Guid.CreateVersion7()), slug, slug, Ct)));

        var redirects = Strings((await JsonAsync("clients?clientId=karry-portal"))[0], "redirectUris");
        redirects.Should().Contain(slugs.Select(s => $"https://{s}.karry.test/*"), "a read-modify-write lost to another one is retried");
    }

    [Fact]
    public async Task An_account_left_disabled_by_a_failed_invitation_is_adopted_but_an_active_one_is_a_conflict()
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        var account = new NewAccount(tenant, $"adopt-{Guid.NewGuid():N}@example.test", null, "Ada", "Opted", MfaRequired: false);

        var first = await provider.CreateAccountAsync(account, Ct);
        var retry = await provider.CreateAccountAsync(account, Ct);
        var otherTenant = await provider.CreateAccountAsync(account with { Tenant = new TenantId(Guid.CreateVersion7()) }, Ct);
        await provider.ActivateAsync(first.Value, "correct-horse-battery-staple", Ct);
        var afterActivation = await provider.CreateAccountAsync(account, Ct);

        retry.Value.Should().Be(first.Value, "the same tenant's disabled account is reused");
        otherTenant.Error!.Code.Should().Be("identity.account_exists", "never take over another tenant's account");
        afterActivation.Error!.Code.Should().Be("identity.account_exists");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-02")]
    public async Task Privileged_accounts_get_the_mfa_role_and_weak_passwords_are_refused_by_the_realm_policy()
    {
        var account = new NewAccount(new TenantId(Guid.CreateVersion7()), $"mfa-{Guid.NewGuid():N}@example.test", null, "Fin", "Ance", MfaRequired: true);
        var subject = (await provider.CreateAccountAsync(account, Ct)).Value;

        var weak = await provider.ActivateAsync(subject, "password", Ct);

        weak.Error!.Code.Should().Be("identity.password_rejected");
        (await JsonAsync($"users/{subject}")).GetProperty("enabled").GetBoolean().Should().BeFalse("a refused password enables nothing");
        (await JsonAsync($"users/{subject}/role-mappings/realm")).EnumerateArray().Select(r => r.GetProperty("name").GetString()).Should().Contain("mfa-required");
    }

    [Fact]
    public async Task Disabling_twice_or_an_unknown_account_is_harmless()
    {
        var subject = (await provider.CreateAccountAsync(
            new NewAccount(new TenantId(Guid.CreateVersion7()), $"off-{Guid.NewGuid():N}@example.test", null, "Le", "Aver", false), Ct)).Value;

        await provider.DisableAndSignOutAsync(subject, Ct);
        await provider.DisableAndSignOutAsync(subject, Ct);
        await provider.DisableAndSignOutAsync(Guid.NewGuid().ToString(), Ct);

        (await JsonAsync($"users/{subject}")).GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    private static List<string> Strings(JsonElement element, string property) =>
        [.. element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];

    private async Task<JsonElement> JsonAsync(string path)
    {
        using var response = await keycloak.Server.AdminAsync(HttpMethod.Get, path, cancellationToken: Ct);
        return await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
