using System.Net;
using System.Text.RegularExpressions;
using Karry.Testing;

namespace Karry.Modules.Identity.Tests.Realm;

/// <summary>
/// A forgotten password on the real realm (hardening plan H-I6): the e-mail link alone resets the password of a person
/// without a privileged role; a privileged person must also pass their second factor first, and a reset never removes
/// a factor. Keycloak's built-in flow did neither, so an inbox was enough to take over a Tenant Admin.
/// </summary>
[Trait("Story", "US-P1-IAM-02")]
public sealed partial class KeycloakPasswordResetTests(KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private const string Password = "correct-horse-battery-staple";
    private const string NewPassword = "a-brand-new-long-passphrase";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private KeycloakServer Server => keycloak.Server;

    [Fact]
    public async Task A_person_without_a_privileged_role_resets_with_the_email_link_alone()
    {
        var username = await NewUserAsync();
        using var browser = new KeycloakBrowser(Server);

        var passwordPage = await OpenLinkAsync(browser, await RequestResetAsync(browser, username));
        passwordPage.HasInput("password-new").Should().BeTrue(passwordPage.Html);
        var done = await browser.SubmitAsync(passwordPage, new Dictionary<string, string> { ["password-new"] = NewPassword, ["password-confirm"] = NewPassword }, Cancel);
        var (status, body) = await browser.ExchangeAsync(done, Cancel);
        status.Should().Be(HttpStatusCode.OK, body.ToString());

        (await SignInAsync(username, NewPassword)).Code.Should().NotBeNull("the new password works");
        (await SignInAsync(username, Password)).Code.Should().BeNull("the old one no longer does");
    }

    [Fact]
    public async Task A_privileged_person_must_pass_their_second_factor_before_choosing_a_password_and_keeps_it()
    {
        var username = await NewUserAsync(privileged: true);
        var secret = await EnrolTotpAsync(username);
        using var browser = new KeycloakBrowser(Server);

        var factorPage = await OpenLinkAsync(browser, await RequestResetAsync(browser, username));
        factorPage.HasInput("otp").Should().BeTrue("the inbox alone is not enough for a privileged account");
        factorPage.HasInput("password-new").Should().BeFalse();

        var wrong = await browser.SubmitAsync(factorPage, new Dictionary<string, string> { ["otp"] = "000000" }, Cancel);
        wrong.HasInput("password-new").Should().BeFalse("a wrong code never reaches the password form");
        wrong.Code.Should().BeNull();

        // The next 30-second window: the code used at enrolment cannot be used twice.
        var passwordPage = await SettleAsync(browser, await browser.SubmitAsync(wrong, new Dictionary<string, string> { ["otp"] = KeycloakBrowser.Totp(secret, DateTimeOffset.UtcNow.AddSeconds(30)) }, Cancel));
        passwordPage.HasInput("password-new").Should().BeTrue(passwordPage.Html);
        var done = await browser.SubmitAsync(passwordPage, new Dictionary<string, string> { ["password-new"] = NewPassword, ["password-confirm"] = NewPassword }, Cancel);
        var (status, tokens) = await browser.ExchangeAsync(done, Cancel);
        status.Should().Be(HttpStatusCode.OK, tokens.ToString());
        KeycloakBrowser.Claims(tokens.GetProperty("access_token").GetString()!).GetProperty("amr").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("otp", "the token says the second factor was used");

        var next = await SignInAsync(username, NewPassword);
        next.Code.Should().BeNull();
        next.HasInput("otp").Should().BeTrue("the reset kept the authenticator: the next sign-in still asks for it");
    }

    [Fact]
    public async Task A_privileged_person_without_a_factor_must_enrol_one_before_any_token_is_issued()
    {
        var username = await NewUserAsync(privileged: true);
        using var browser = new KeycloakBrowser(Server);

        var passwordPage = await OpenLinkAsync(browser, await RequestResetAsync(browser, username));
        var afterPassword = await browser.SubmitAsync(passwordPage, new Dictionary<string, string> { ["password-new"] = NewPassword, ["password-confirm"] = NewPassword }, Cancel);

        afterPassword.Code.Should().BeNull("no token before the second factor is enrolled");
        var enrolment = afterPassword.Location is null ? afterPassword : await browser.FollowAsync(afterPassword, Cancel);
        enrolment.HasInput("totpSecret").Should().BeTrue(enrolment.Html);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled")]
    public async Task The_answer_never_says_whether_an_account_exists_and_no_mail_leaves_for_a_missing_one(string kind)
    {
        using var known = new KeycloakBrowser(Server);
        var knownAnswer = await RequestAnswerAsync(known, await NewUserAsync());

        string target;
        if (kind == "unknown")
        {
            target = $"u{Guid.NewGuid():N}"[..20];
        }
        else
        {
            target = await NewUserAsync();
            var id = await UserIdAsync(target);
            using var disabled = await Server.AdminAsync(HttpMethod.Put, $"users/{id}", new { enabled = false }, Cancel);
            disabled.EnsureSuccessStatusCode();
        }

        using var browser = new KeycloakBrowser(Server);
        var answer = await RequestAnswerAsync(browser, target);

        Message(answer).Should().Be(Message(knownAnswer), "the same words whether or not the account exists (no account enumeration)");
        (await Server.MailToAsync($"{target}@example.test", TimeSpan.FromSeconds(3), Cancel)).Should().BeNull();
    }

    private async Task<Uri> RequestResetAsync(KeycloakBrowser browser, string username)
    {
        await RequestAnswerAsync(browser, username);
        var mail = await Server.MailToAsync($"{username}@example.test", cancellationToken: Cancel);
        mail.Should().NotBeNull("Keycloak sends the reset e-mail");
        return new Uri(ResetLink().Match(mail!).Value);
    }

    /// <summary>Opens the e-mail's link and follows Keycloak's redirects to the page it leads to.</summary>
    private async Task<LoginStep> OpenLinkAsync(KeycloakBrowser browser, Uri link) => await SettleAsync(browser, await browser.OpenAsync(link, Cancel));

    /// <summary>Follows redirects inside Keycloak until a page (or the final redirect to the app).</summary>
    private async Task<LoginStep> SettleAsync(KeycloakBrowser browser, LoginStep page)
    {
        while (page.Location is { } next && next.Authority == Server.BaseUrl.Authority)
        {
            page = await browser.FollowAsync(page, Cancel);
        }

        return page;
    }

    /// <summary>Opens "forgot password" from the login page and submits the username; returns the page shown after.</summary>
    private static async Task<LoginStep> RequestAnswerAsync(KeycloakBrowser browser, string username)
    {
        var login = await browser.StartAsync(cancellationToken: Cancel);
        var forgot = await browser.OpenAsync(login.Link("reset-credentials") ?? throw new InvalidOperationException("No forgot-password link."), Cancel);
        var answer = await browser.SubmitAsync(forgot, new Dictionary<string, string> { ["username"] = username }, Cancel);
        return answer.Location is null ? answer : await browser.FollowAsync(answer, Cancel);
    }

    private static string Message(LoginStep page) =>
        WebUtility.HtmlDecode(Regex.Replace(PageMessage().Match(page.Html).Groups[1].Value, "<[^>]+>", string.Empty)).Trim();

    private async Task<LoginStep> SignInAsync(string username, string password)
    {
        using var browser = new KeycloakBrowser(Server);
        return await browser.LoginAsync(await browser.StartAsync(cancellationToken: Cancel), username, password, Cancel);
    }

    private async Task<string> EnrolTotpAsync(string username)
    {
        using var browser = new KeycloakBrowser(Server);
        var enrolment = await browser.FollowAsync(await browser.LoginAsync(await browser.StartAsync(cancellationToken: Cancel), username, Password, Cancel), Cancel);
        var secret = enrolment.Input("totpSecret")!;
        var enrolled = await browser.SubmitAsync(enrolment, new Dictionary<string, string>
        {
            ["totp"] = KeycloakBrowser.Totp(secret, DateTimeOffset.UtcNow),
            ["userLabel"] = "phone",
        }, Cancel);
        enrolled.Code.Should().NotBeNull(enrolled.Html);
        return secret;
    }

    private async Task<string> UserIdAsync(string username)
    {
        using var found = await Server.AdminAsync(HttpMethod.Get, $"users?username={username}&exact=true", cancellationToken: Cancel);
        var users = System.Text.Json.JsonDocument.Parse(await found.EnsureSuccessStatusCode().Content.ReadAsStringAsync(Cancel)).RootElement;
        return users[0].GetProperty("id").GetString()!;
    }

    private async Task<string> NewUserAsync(bool privileged = false)
    {
        var username = $"u{Guid.NewGuid():N}"[..20];
        await Server.CreateUserAsync(username, Password, realmRoles: privileged ? ["mfa-required"] : null, cancellationToken: Cancel);
        return username;
    }

    [GeneratedRegex(@"https?://\S+/login-actions/action-token\?\S+")]
    private static partial Regex ResetLink();

    [GeneratedRegex("<div[^>]*(?:alert|feedback)[^>]*>(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex PageMessage();
}
