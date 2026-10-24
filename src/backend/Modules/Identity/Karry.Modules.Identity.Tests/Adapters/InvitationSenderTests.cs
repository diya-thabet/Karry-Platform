using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Identity.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Karry.Modules.Identity.Tests.Adapters;

/// <summary>Mailpit (same image as the local stack), the SMTP catcher the invitation e-mails go to locally.</summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    public const string Image = "axllent/mailpit:v1.31.6";

    private IContainer? container;

    public string Host => container!.Hostname;

    public int SmtpPort => container!.GetMappedPublicPort(1025);

    public Uri Api => new($"http://{container!.Hostname}:{container.GetMappedPublicPort(8025)}/api/v1/");

    public async ValueTask InitializeAsync()
    {
        container = new ContainerBuilder(Image)
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/readyz")))
            .Build();
        await container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}

[Trait("Story", "US-P1-IAM-03")]
public sealed class InvitationSenderTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>, IDisposable
{
    private static readonly Uri Link = new("https://acme.karry.test/invitation#token=secret-token");
    private readonly HttpClient http = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => http.Dispose();

    [Fact]
    public async Task An_email_invitation_reaches_the_invitee_in_french_and_english_with_the_link()
    {
        var to = $"{Guid.NewGuid():N}@example.test";

        (await Sender().SendAsync(Message(InvitationChannel.Email, to), Ct)).Should().BeTrue();

        var mail = await MessageToAsync(to);
        mail.GetProperty("Subject").GetString().Should().Be("Invitation Karry · Carrière Acme");
        mail.GetProperty("From").GetProperty("Address").GetString().Should().Be("no-reply@karry.test");
        mail.GetProperty("Text").GetString().Should().Contain("Bonjour Amel Ben Salah").And.Contain("Hello Amel Ben Salah")
            .And.Contain(Link.ToString()).And.Contain("2026-10-31 08:00 UTC");
    }

    [Fact]
    public async Task Phone_invitations_are_previewed_in_mailpit_only_when_asked_and_refused_otherwise()
    {
        var number = $"+2169{Random.Shared.Next(1000000, 9999999)}";

        (await Sender(phoneMode: "email-preview").SendAsync(Message(InvitationChannel.Phone, number), Ct)).Should().BeTrue();
        (await Sender().SendAsync(Message(InvitationChannel.Phone, number), Ct)).Should().BeFalse("no SMS provider yet (owner input)");

        (await MessageToAsync($"sms+{number.TrimStart('+')}@sms.karry.local")).GetProperty("Text").GetString().Should().Contain(Link.ToString());
    }

    [Fact]
    public async Task Without_a_server_or_with_an_unreachable_one_delivery_reports_false_instead_of_failing()
    {
        var unconfigured = new InvitationSender(Options.Create(new MessagingOptions()), NullLogger<InvitationSender>.Instance);
        var unreachable = new InvitationSender(Options.Create(new MessagingOptions { SmtpHost = "127.0.0.1", SmtpPort = 1, RequireTls = false }),
            NullLogger<InvitationSender>.Instance);

        (await unconfigured.SendAsync(Message(InvitationChannel.Email, "a@example.test"), Ct)).Should().BeFalse();
        (await unreachable.SendAsync(Message(InvitationChannel.Email, "a@example.test"), Ct)).Should().BeFalse();
    }

    private static InvitationMessage Message(InvitationChannel channel, string destination) =>
        new(channel, destination, "Amel Ben Salah", "Carrière Acme", Link, new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero));

    private InvitationSender Sender(string phoneMode = "none") =>
        new(Options.Create(new MessagingOptions
        {
            SmtpHost = mailpit.Host,
            SmtpPort = mailpit.SmtpPort,
            RequireTls = false,
            From = "Karry <no-reply@karry.test>",
            PhoneMode = phoneMode,
        }), NullLogger<InvitationSender>.Instance);

    private async Task<JsonElement> MessageToAsync(string address)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var found = await http.GetFromJsonAsync<JsonElement>(new Uri(mailpit.Api, $"search?query={Uri.EscapeDataString($"to:\"{address}\"")}"), Ct);
            if (found.GetProperty("messages").GetArrayLength() > 0)
            {
                var id = found.GetProperty("messages")[0].GetProperty("ID").GetString();
                return await http.GetFromJsonAsync<JsonElement>(new Uri(mailpit.Api, $"message/{id}"), Ct);
            }

            await Task.Delay(100, Ct);
        }

        throw new InvalidOperationException($"No message to {address} in Mailpit.");
    }
}
