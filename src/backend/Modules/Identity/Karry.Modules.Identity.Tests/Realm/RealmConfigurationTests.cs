using System.Text.Json.Nodes;
using Karry.Testing;

namespace Karry.Modules.Identity.Tests.Realm;

/// <summary>
/// The realm file is the security configuration of sign-in (ADR-0012): these checks fail the build when a change weakens
/// it, before any Keycloak runs. The live behaviour is proven by <see cref="KeycloakLoginTests"/>.
/// </summary>
[Trait("Story", "US-P1-IAM-01")]
public sealed class RealmConfigurationTests
{
    private static readonly string KeycloakDirectory = Path.Combine(PostgresServer.RepositoryRoot(), "deploy", "keycloak");
    private static readonly JsonNode Realm = JsonNode.Parse(File.ReadAllText(Path.Combine(KeycloakDirectory, "realm", "karry-realm.json")))!;

    public static TheoryData<string> AppClients => ["karry-portal"];

    [Fact]
    public void Tokens_are_short_lived_and_refresh_tokens_are_single_use()
    {
        Realm["accessTokenLifespan"]!.GetValue<int>().Should().BeInRange(60, 600, "AC2: access tokens live at most 10 minutes");
        Realm["revokeRefreshToken"]!.GetValue<bool>().Should().BeTrue("AC2: refresh tokens rotate");
        Realm["refreshTokenMaxReuse"]!.GetValue<int>().Should().Be(0, "AC2: any reuse is detected");
        Realm["ssoSessionMaxLifespan"]!.GetValue<int>().Should().BeLessThanOrEqualTo(12 * 3600, "a working day, then sign in again");
    }

    [Fact]
    [Trait("Story", "US-P1-OFF-02")]
    public void The_field_app_has_no_client_because_tablets_sign_their_requests_as_devices()
    {
        Realm["clients"]!.AsArray().Select(c => c!["clientId"]!.GetValue<string>()).Should().NotContain("karry-field", "an unused client with redirect URIs is attack surface (ADR-0028 §1)");
    }

    [Theory]
    [MemberData(nameof(AppClients))]
    public void App_clients_are_public_and_use_only_the_authorization_code_flow_with_pkce(string clientId)
    {
        var client = Client(clientId);

        client["publicClient"]!.GetValue<bool>().Should().BeTrue("browser apps cannot keep a secret");
        client["standardFlowEnabled"]!.GetValue<bool>().Should().BeTrue();
        client["implicitFlowEnabled"]!.GetValue<bool>().Should().BeFalse();
        client["directAccessGrantsEnabled"]!.GetValue<bool>().Should().BeFalse("passwords are only ever typed into Keycloak");
        client["attributes"]!["pkce.code.challenge.method"]!.GetValue<string>().Should().Be("S256");
        client["fullScopeAllowed"]!.GetValue<bool>().Should().BeFalse("app tokens carry no Keycloak roles; authorization lives in Karry");
    }

    [Theory]
    [MemberData(nameof(AppClients))]
    public void App_clients_only_redirect_to_explicit_origins(string clientId)
    {
        var client = Client(clientId);
        var redirects = client["redirectUris"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        var origins = client["webOrigins"]!.AsArray().Select(o => o!.GetValue<string>()).ToList();

        redirects.Should().NotBeEmpty().And.OnlyContain(r => r.StartsWith("http", StringComparison.Ordinal) && r.EndsWith("/*", StringComparison.Ordinal));
        redirects.Select(r => new Uri(r.TrimEnd('*')).Host).Should().NotContain(h => h.Contains('*', StringComparison.Ordinal));
        origins.Should().NotContain(["*", "+"], "CORS for tokens is limited to the apps' own origins");
    }

    [Theory]
    [MemberData(nameof(AppClients))]
    public void App_tokens_carry_the_tenant_and_platform_role_attributes_and_the_api_audience(string clientId)
    {
        var mappers = Client(clientId)["protocolMappers"]!.AsArray();

        Claim(mappers, "tenant_id").Should().Be("tenant_id", "TenantResolutionMiddleware reads exactly this claim");
        Claim(mappers, "platform_role").Should().Be("platform_role", "KarryPolicies.PlatformOperator reads exactly this claim");
        mappers.Should().Contain(m => m!["protocolMapper"]!.GetValue<string>() == "oidc-audience-mapper"
            && m["config"]!["included.client.audience"]!.GetValue<string>() == "karry-api");
    }

    [Fact]
    public void Tenant_and_platform_role_attributes_can_only_be_seen_and_changed_by_administrators()
    {
        var profile = JsonNode.Parse(Realm["components"]!["org.keycloak.userprofile.UserProfileProvider"]![0]!["config"]!["kc.user.profile.config"]![0]!.GetValue<string>())!;
        var attributes = profile["attributes"]!.AsArray();

        foreach (var name in new[] { "tenant_id", "platform_role" })
        {
            var permissions = attributes.Single(a => a!["name"]!.GetValue<string>() == name)!["permissions"]!;
            permissions["edit"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal(["admin"], $"a user must never move to another tenant ({name})");
        }

        profile["unmanagedAttributePolicy"]?.GetValue<string>().Should().NotBe("ENABLED", "users cannot add attributes of their own");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-01")]
    public void Brute_force_protection_locks_accounts_after_a_few_failures()
    {
        Realm["bruteForceProtected"]!.GetValue<bool>().Should().BeTrue("AC4");
        Realm["failureFactor"]!.GetValue<int>().Should().Be(5, "hardening plan H-I5: locked after 5 failures");
        Realm["maxFailureWaitSeconds"]!.GetValue<int>().Should().Be(900, "H-I5: for up to 15 minutes");
        Realm["waitIncrementSeconds"]!.GetValue<int>().Should().BeInRange(30, 300);
        Realm["quickLoginCheckMilliSeconds"]!.GetValue<int>().Should().BePositive("scripted guessing (faster than a person types) is slowed down too");
        Realm["permanentLockout"]!.GetValue<bool>().Should().BeFalse("a temporary lockout cannot be used to lock a colleague out for good");
        Realm["enabledEventTypes"]!.AsArray().Select(e => e!.GetValue<string>()).Should().Contain(["LOGIN", "LOGIN_ERROR"], "AC4: failures are recorded");
    }

    [Fact]
    public void Sign_in_rules_for_a_business_tenant()
    {
        Realm["registrationAllowed"]!.GetValue<bool>().Should().BeFalse("users are invited, never self-registered");
        Realm["sslRequired"]!.GetValue<string>().Should().BeOneOf("external", "all");
        Realm["passwordPolicy"]!.GetValue<string>().Should().Contain("length(12)").And.Contain("notUsername").And.Contain("passwordHistory");
        Realm["supportedLocales"]!.AsArray().Select(l => l!.GetValue<string>()).Should().BeEquivalentTo(["fr", "en"], "AC3");
        Realm["defaultLocale"]!.GetValue<string>().Should().Be("fr");
        Realm["loginTheme"]!.GetValue<string>().Should().Be("karry");
        Realm["organizationsEnabled"]!.GetValue<bool>().Should().BeTrue("one organization per tenant (ADR-0012)");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-02")]
    public void The_browser_flow_requires_a_second_factor_for_the_mfa_required_role()
    {
        Realm["browserFlow"]!.GetValue<string>().Should().Be("karry browser");
        var flows = Realm["authenticationFlows"]!.AsArray().ToDictionary(f => f!["alias"]!.GetValue<string>(), f => f!["authenticationExecutions"]!.AsArray());
        var configs = Realm["authenticatorConfig"]!.AsArray().ToDictionary(c => c!["alias"]!.GetValue<string>(), c => c!["config"]!);

        var privileged = flows["karry privileged mfa"];
        var condition = privileged.Single(e => e!["authenticator"]?.GetValue<string>() == "conditional-user-role")!;
        configs[condition["authenticatorConfig"]!.GetValue<string>()]["condUserRole"]!.GetValue<string>().Should().Be("mfa-required");
        flows["karry enrol factor"].Should().Contain(e => e!["authenticator"]!.GetValue<string>() == "auth-otp-form" && e["requirement"]!.GetValue<string>() == "REQUIRED",
            "no configured factor: TOTP enrolment is forced");
        flows["karry configured factor"].Select(e => e!["authenticator"]!.GetValue<string>()).Should().Contain(["webauthn-authenticator", "auth-otp-form"]);
        Realm["roles"]!["realm"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Should().Contain("mfa-required");
    }

    [Theory]
    [MemberData(nameof(AppClients))]
    [Trait("Story", "US-P1-OPS-03")]
    public void App_tokens_say_which_factors_were_used_and_every_factor_names_itself(string clientId)
    {
        Client(clientId)["protocolMappers"]!.AsArray().Should().Contain(m => m!["protocolMapper"]!.GetValue<string>() == "oidc-amr-mapper"
            && m["config"]!["access.token.claim"]!.GetValue<string>() == "true", "KarryPolicies reads amr from the access token");
        var configs = Realm["authenticatorConfig"]!.AsArray().ToDictionary(c => c!["alias"]!.GetValue<string>(), c => c!["config"]!);
        var expected = new Dictionary<string, string> { ["auth-username-password-form"] = "pwd", ["auth-otp-form"] = "otp", ["webauthn-authenticator"] = "hwk" };
        var factors = Realm["authenticationFlows"]!.AsArray().SelectMany(f => f!["authenticationExecutions"]!.AsArray())
            .Where(e => e!["authenticator"] is { } a && expected.ContainsKey(a.GetValue<string>())).ToList();

        factors.Should().HaveCount(7, "password, hardware key and the one-time password asked or enrolled at sign-in; the same factors at a password reset");
        foreach (var factor in factors)
        {
            var reference = configs[factor!["authenticatorConfig"]!.GetValue<string>()];
            reference["default.reference.value"]!.GetValue<string>().Should().Be(expected[factor["authenticator"]!.GetValue<string>()]);
            reference["default.reference.maxAge"]!.GetValue<string>().Should().Be(Realm["ssoSessionMaxLifespan"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "a factor counts for the whole SSO session, never longer");
        }
    }

    [Fact]
    public void Secrets_come_from_the_environment_with_local_only_defaults()
    {
        var identity = Client("karry-identity");

        identity["secret"]!.GetValue<string>().Should().MatchRegex(@"^\$\{KARRY_IDENTITY_CLIENT_SECRET:[a-z-]+-local-only\}$");
        identity["serviceAccountsEnabled"]!.GetValue<bool>().Should().BeTrue();
        identity["standardFlowEnabled"]!.GetValue<bool>().Should().BeFalse();
        identity["fullScopeAllowed"]!.GetValue<bool>().Should().BeFalse("its token carries only the realm-management roles it is given");
        string[] needed = ["manage-users", "view-users", "query-users", "manage-realm", "view-realm", "view-events", "view-clients", "manage-clients"];
        Realm["users"]!.AsArray().Single(u => u!["serviceAccountClientId"]?.GetValue<string>() == "karry-identity")!["clientRoles"]!["realm-management"]!
            .AsArray().Select(r => r!.GetValue<string>()).Should().BeEquivalentTo(needed,
                "least privilege: users, organizations (manage-realm), login events, and app redirect URIs (manage-clients); nothing else");
        File.ReadAllText(Path.Combine(KeycloakDirectory, "realm", "karry-realm.json")).Should().NotContainAny(["\"password\"", "\"value\""], "no user credentials in the realm file");
    }

    [Fact]
    public void Tests_use_the_same_keycloak_and_mailpit_images_as_the_local_stack()
    {
        var compose = File.ReadAllText(Path.Combine(PostgresServer.RepositoryRoot(), "deploy", "docker", "compose.yaml"));

        compose.Should().Contain($"image: {KeycloakServer.Image}").And.Contain($"image: {KeycloakServer.MailpitImage}");
    }

    [Fact]
    [Trait("Story", "US-P1-IAM-02")]
    public void A_forgotten_password_needs_the_email_link_then_the_second_factor_of_a_privileged_user_and_never_removes_a_factor()
    {
        // H-I6: Keycloak's built-in "reset credentials" flow asks no second factor and removes the OTP, so the inbox alone
        // took over a privileged account (KeycloakPasswordResetTests proves both on a live Keycloak).
        Realm["resetCredentialsFlow"]!.GetValue<string>().Should().Be("karry reset credentials");
        var flows = Realm["authenticationFlows"]!.AsArray().ToDictionary(f => f!["alias"]!.GetValue<string>(), f => f!["authenticationExecutions"]!.AsArray());
        var configs = Realm["authenticatorConfig"]!.AsArray().ToDictionary(c => c!["alias"]!.GetValue<string>(), c => c!["config"]!);
        static string Name(JsonNode? e) => e!["authenticatorFlow"]!.GetValue<bool>() ? e["flowAlias"]!.GetValue<string>() : e["authenticator"]!.GetValue<string>();

        flows["karry reset credentials"].OrderBy(e => e!["priority"]!.GetValue<int>()).Select(Name).Should().Equal(
            ["reset-credentials-choose-user", "reset-credential-email", "karry reset privileged mfa", "reset-password"],
            "the e-mail proves the inbox, the second factor comes before the password can be chosen");
        var condition = flows["karry reset privileged mfa"].Single(e => e!["authenticator"]?.GetValue<string>() == "conditional-user-role")!;
        configs[condition["authenticatorConfig"]!.GetValue<string>()]["condUserRole"]!.GetValue<string>().Should().Be("mfa-required");
        flows["karry reset configured factor"].Select(Name).Should().Contain(["webauthn-authenticator", "auth-otp-form"]);
        flows["karry reset enrol factor"].Should().Contain(e => e!["authenticator"]!.GetValue<string>() == "auth-otp-form" && e["requirement"]!.GetValue<string>() == "REQUIRED");
        configs["karry reset no configured factor"]["flow_to_check"]!.GetValue<string>().Should().Be("karry reset configured factor");
        flows.Values.SelectMany(f => f).Select(Name).Should().NotContain("reset-otp", "a password reset never removes a second factor");
        Realm["enabledEventTypes"]!.AsArray().Select(e => e!.GetValue<string>()).Should().Contain(["SEND_RESET_PASSWORD", "RESET_PASSWORD", "RESET_PASSWORD_ERROR"]);
    }

    [Fact]
    public void Keycloak_mail_settings_come_from_the_environment_and_carry_no_credentials()
    {
        var smtp = Realm["smtpServer"]!.AsObject();

        smtp["host"]!.GetValue<string>().Should().Be("${KARRY_SMTP_HOST:mailpit}", "Mailpit locally, the real relay from the environment in production");
        smtp["from"]!.GetValue<string>().Should().StartWith("${KARRY_SMTP_FROM:");
        smtp.Select(p => p.Key).Should().NotContain(["user", "password"], "relay credentials live in the vault (P1.17), never in the repository");
    }

    [Fact]
    public void No_client_accepts_passwords_or_the_implicit_flow_and_only_the_identity_module_has_a_service_account()
    {
        foreach (var client in Realm["clients"]!.AsArray())
        {
            var id = client!["clientId"]!.GetValue<string>();
            client["directAccessGrantsEnabled"]?.GetValue<bool>().Should().NotBe(true, $"{id}: passwords are only ever typed into Keycloak");
            client["implicitFlowEnabled"]?.GetValue<bool>().Should().NotBe(true, $"{id}: tokens never travel in a URL fragment");
            client["serviceAccountsEnabled"]?.GetValue<bool>().Should().Be(id == "karry-identity", $"{id}: service accounts are the Identity module's only");
            client["defaultClientScopes"]?.AsArray().Select(s => s!.GetValue<string>()).Should().NotContain("offline_access", $"{id}: no tokens that outlive the SSO session");
            client["optionalClientScopes"]?.AsArray().Select(s => s!.GetValue<string>()).Should().NotContain("offline_access", $"{id}: no tokens that outlive the SSO session");
        }

        int.Parse(Client("karry-identity")["attributes"]!["access.token.lifespan"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeLessThanOrEqualTo(300, "the most powerful token of the realm lives a few minutes");
    }

    [Fact]
    public void The_one_time_password_policy_is_what_every_authenticator_app_computes()
    {
        // HMAC-SHA1 is RFC 6238's default and the only algorithm every authenticator app gets right (several silently
        // fall back to SHA-1, and the codes never match). SHA-1's collision weakness does not apply to HMAC.
        Realm["otpPolicyType"]!.GetValue<string>().Should().Be("totp");
        Realm["otpPolicyAlgorithm"]!.GetValue<string>().Should().Be("HmacSHA1");
        Realm["otpPolicyDigits"]!.GetValue<int>().Should().Be(6);
        Realm["otpPolicyPeriod"]!.GetValue<int>().Should().Be(30);
        Realm["otpPolicyLookAheadWindow"]!.GetValue<int>().Should().BeLessThanOrEqualTo(1, "one window of clock drift, no more guesses than that");
    }

    [Fact]
    public void The_login_theme_is_translated_in_both_supported_languages()
    {
        var messages = Path.Combine(KeycloakDirectory, "themes", "karry", "login", "messages");
        static HashSet<string> Keys(string file) =>
            File.ReadAllLines(file).Where(l => l.Contains('=', StringComparison.Ordinal) && !l.StartsWith('#')).Select(l => l.Split('=')[0]).ToHashSet();

        Keys(Path.Combine(messages, "messages_fr.properties")).Should().BeEquivalentTo(Keys(Path.Combine(messages, "messages_en.properties")));
        File.ReadAllText(Path.Combine(KeycloakDirectory, "themes", "karry", "login", "theme.properties")).Should().Contain("locales=fr,en");
    }

    private static JsonNode Client(string clientId) =>
        Realm["clients"]!.AsArray().Single(c => c!["clientId"]!.GetValue<string>() == clientId)!;

    private static string? Claim(JsonArray mappers, string attribute) =>
        mappers.SingleOrDefault(m => m!["protocolMapper"]!.GetValue<string>() == "oidc-usermodel-attribute-mapper"
            && m["config"]!["user.attribute"]!.GetValue<string>() == attribute)?["config"]!["claim.name"]!.GetValue<string>();
}
