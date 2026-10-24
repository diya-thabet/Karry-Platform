using System.Net;
using System.Text.Json;
using Karry.Testing;

namespace Karry.Modules.Identity.Tests.Realm;

/// <summary>One Keycloak (repository realm and theme) for the live sign-in tests.</summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public KeycloakServer Server { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Server = await KeycloakServer.StartAsync();

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>
/// Sign-in against the real realm (US-P1-IAM-01, US-P1-IAM-02; TC-P1-IAM-002, TC-P1-IAM-003): every rule the realm file
/// declares is observed on a running Keycloak, through the same Authorization Code + PKCE requests the apps make.
/// </summary>
public sealed class KeycloakLoginTests(KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private const string Password = "correct-horse-battery-staple";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private KeycloakServer Server => keycloak.Server;

    [Fact]
    [Trait("Story", "US-P1-IAM-01")]
    public async Task Login_with_pkce_issues_a_short_lived_api_token_with_the_tenant_claim()
    {
        var tenant = Guid.NewGuid().ToString();
        var username = await NewUserAsync(tenant);
        using var browser = new KeycloakBrowser(Server);

        var tokens = await SignInAsync(browser, username);

        var claims = KeycloakBrowser.Claims(tokens.GetProperty("access_token").GetString()!);
        claims.GetProperty("iss").GetString().Should().Be(Server.Issuer);
        claims.GetProperty("aud").GetString().Should().Be("karry-api", "the API validates this audience");
        claims.GetProperty("azp").GetString().Should().Be("karry-portal");
        claims.GetProperty("tenant_id").GetString().Should().Be(tenant);
        claims.TryGetProperty("platform_role", out _).Should().BeFalse();
        claims.TryGetProperty("realm_access", out _).Should().BeFalse("roles and permissions come from Karry, not from the token");
        (claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64()).Should().BeLessThanOrEqualTo(600, "AC2");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-01")]
    public async Task A_platform_operator_token_carries_the_operator_role_and_no_tenant()
    {
        var username = await NewUserAsync(attributes: new Dictionary<string, string> { ["platform_role"] = "operator" });
        using var browser = new KeycloakBrowser(Server);

        var claims = KeycloakBrowser.Claims((await SignInAsync(browser, username)).GetProperty("access_token").GetString()!);

        claims.GetProperty("platform_role").GetString().Should().Be("operator");
        claims.TryGetProperty("tenant_id", out _).Should().BeFalse();
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-01")]
    public async Task Passwords_are_never_accepted_outside_the_login_page_and_pkce_is_mandatory()
    {
        var username = await NewUserAsync(Guid.NewGuid().ToString());
        using var browser = new KeycloakBrowser(Server);

        var (status, body) = await browser.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "karry-portal",
            ["username"] = username,
            ["password"] = Password,
        }, Cancel);
        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().Should().Be("unauthorized_client");

        var withoutPkce = await browser.StartAsync(new Dictionary<string, string?> { ["code_challenge"] = null, ["code_challenge_method"] = null }, Cancel);
        withoutPkce.Location.Should().NotBeNull();
        withoutPkce.Location!.Query.Should().Contain("error=invalid_request").And.Contain("code_challenge_method");

        var foreignRedirect = await new KeycloakBrowser(Server, appOrigin: "https://evil.example").StartAsync(cancellationToken: Cancel);
        foreignRedirect.Status.Should().Be(HttpStatusCode.BadRequest, "unregistered redirect URIs get an error page, never a code");
        foreignRedirect.Location.Should().BeNull();
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-01")]
    [Trait("TestCase", "TC-P1-IAM-003")]
    public async Task Refresh_tokens_rotate_and_reusing_one_revokes_the_session()
    {
        using var browser = new KeycloakBrowser(Server);
        var tokens = await SignInAsync(browser, await NewUserAsync(Guid.NewGuid().ToString()));
        var first = tokens.GetProperty("refresh_token").GetString()!;

        var (rotated, next) = await browser.RefreshAsync(first, Cancel);
        rotated.Should().Be(HttpStatusCode.OK);
        var second = next.GetProperty("refresh_token").GetString()!;
        second.Should().NotBe(first);

        var (reused, error) = await browser.RefreshAsync(first, Cancel);
        reused.Should().Be(HttpStatusCode.BadRequest);
        error.GetProperty("error").GetString().Should().Be("invalid_grant");

        var (afterReuse, _) = await browser.RefreshAsync(second, Cancel);
        afterReuse.Should().Be(HttpStatusCode.BadRequest, "the reuse revoked the session: the newest token (possibly the thief's) is dead too");
    }

    [Theory]
    [Trait("Story", "US-P1-IAM-01")]
    [InlineData(null, "fr", "Connectez-vous à Karry")]
    [InlineData("en", "en", "Sign in to Karry")]
    [InlineData("fr", "fr", "Connectez-vous à Karry")]
    public async Task The_login_page_is_french_by_default_and_english_on_request(string? uiLocale, string expectedLang, string heading)
    {
        using var browser = new KeycloakBrowser(Server);

        var page = await browser.StartAsync(new Dictionary<string, string?> { ["ui_locales"] = uiLocale }, Cancel);

        page.Status.Should().Be(HttpStatusCode.OK);
        page.Html.Should().Contain($"lang=\"{expectedLang}\"").And.Contain(heading);
        page.Html.Should().Contain("/login/karry/js/karry-branding.js", "AC3: the tenant branding script is part of the theme");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-01")]
    public async Task Repeated_failures_lock_the_account_temporarily_and_every_attempt_is_an_event()
    {
        var username = await NewUserAsync(Guid.NewGuid().ToString());
        using var browser = new KeycloakBrowser(Server);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await browser.LoginAsync(await browser.StartAsync(cancellationToken: Cancel), username, "wrong-password-123", Cancel)).Code.Should().BeNull();
        }

        var locked = await browser.LoginAsync(await browser.StartAsync(cancellationToken: Cancel), username, Password, Cancel);

        locked.Code.Should().BeNull("AC4: the right password does not help while the account is locked");
        locked.Html.Should().Contain("mot de passe invalide", "the page says what a wrong password says: an attacker learns nothing");
        var userId = await UserIdAsync(username);
        using var status = await Server.AdminAsync(HttpMethod.Get, $"attack-detection/brute-force/users/{userId}", cancellationToken: Cancel);
        (await JsonAsync(status)).GetProperty("disabled").GetBoolean().Should().BeTrue();
        using var events = await Server.AdminAsync(HttpMethod.Get, $"events?user={userId}&type=LOGIN_ERROR&max=50", cancellationToken: Cancel);
        var errors = (await JsonAsync(events)).EnumerateArray().Select(e => e.GetProperty("error").GetString()).ToList();
        errors.Should().Contain("invalid_user_credentials").And.Contain("user_temporarily_disabled", "the lockout is an event Karry audits");
        errors.Should().HaveCount(6);
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-02")]
    [Trait("TestCase", "TC-P1-IAM-002")]
    public async Task A_privileged_user_without_a_second_factor_must_enrol_totp_before_any_token_is_issued()
    {
        var username = await NewUserAsync(Guid.NewGuid().ToString(), roles: ["mfa-required"]);
        using var browser = new KeycloakBrowser(Server);

        var afterPassword = await browser.LoginAsync(await browser.StartAsync(cancellationToken: Cancel), username, Password, Cancel);

        afterPassword.Code.Should().BeNull("AC2: no code, so no token and no portal access, before enrolment");
        afterPassword.Location!.Query.Should().Contain("execution=CONFIGURE_TOTP");
        var enrolment = await browser.FollowAsync(afterPassword, Cancel);
        var secret = enrolment.Input("totpSecret")!;
        var enrolled = await browser.SubmitAsync(enrolment, new Dictionary<string, string>
        {
            ["totp"] = KeycloakBrowser.Totp(secret, DateTimeOffset.UtcNow),
            ["userLabel"] = "phone",
        }, Cancel);
        var (status, _) = await browser.ExchangeAsync(enrolled, Cancel);
        status.Should().Be(HttpStatusCode.OK, "AC1: enrolment done, the user is signed in");

        using var next = new KeycloakBrowser(Server);
        var secondLogin = await next.LoginAsync(await next.StartAsync(cancellationToken: Cancel), username, Password, Cancel);
        secondLogin.Code.Should().BeNull();
        secondLogin.HasInput("otp").Should().BeTrue("from now on every sign-in asks for the second factor");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-02")]
    public async Task Users_without_a_privileged_role_are_not_asked_for_a_second_factor()
    {
        using var browser = new KeycloakBrowser(Server);

        var tokens = await SignInAsync(browser, await NewUserAsync(Guid.NewGuid().ToString()));

        tokens.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    private static async Task<JsonElement> SignInAsync(KeycloakBrowser browser, string username)
    {
        var final = await browser.LoginAsync(await browser.StartAsync(cancellationToken: Cancel), username, Password, Cancel);
        var (status, body) = await browser.ExchangeAsync(final, Cancel);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return body;
    }

    private async Task<string> NewUserAsync(string? tenant = null, IReadOnlyDictionary<string, string>? attributes = null, IReadOnlyCollection<string>? roles = null)
    {
        var username = $"u{Guid.NewGuid():N}"[..20];
        var all = new Dictionary<string, string>(attributes ?? new Dictionary<string, string>());
        if (tenant is not null)
        {
            all["tenant_id"] = tenant;
        }

        await Server.CreateUserAsync(username, Password, all, roles, Cancel);
        return username;
    }

    private async Task<string> UserIdAsync(string username)
    {
        using var found = await Server.AdminAsync(HttpMethod.Get, $"users?username={username}&exact=true", cancellationToken: Cancel);
        return (await JsonAsync(found))[0].GetProperty("id").GetString()!;
    }
}
