using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Karry.Testing;

/// <summary>
/// Mailpit, the same image as the local stack, catching every e-mail the platform sends (ADR-0039 §4): its SMTP port for
/// the sender and its API to read what arrived. Test projects wrap it in their own fixture.
/// </summary>
public sealed class MailpitServer : IAsyncDisposable
{
    public const string Image = "axllent/mailpit:v1.31.6";

    private readonly IContainer container;
    private readonly HttpClient http = new();

    private MailpitServer(IContainer container)
    {
        this.container = container;
    }

    public string Host => container.Hostname;

    public int SmtpPort => container.GetMappedPublicPort(1025);

    public Uri Api => new($"http://{container.Hostname}:{container.GetMappedPublicPort(8025)}/api/v1/");

    /// <summary>The settings of the platform's sender (<c>Identity:Messaging</c>) pointing at this Mailpit.</summary>
    public IReadOnlyDictionary<string, string?> SenderSettings => new Dictionary<string, string?>
    {
        ["Identity:Messaging:SmtpHost"] = Host,
        ["Identity:Messaging:SmtpPort"] = SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Identity:Messaging:RequireTls"] = "false",
        ["Identity:Messaging:From"] = "Karry <no-reply@karry.test>",
    };

    public static async Task<MailpitServer> StartAsync()
    {
        var container = new ContainerBuilder(Image)
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/readyz")))
            .Build();
        await container.StartAsync().ConfigureAwait(false);
        return new MailpitServer(container);
    }

    /// <summary>The newest message to <paramref name="address"/> (subject, text, …), waiting up to two seconds for it.</summary>
    public async Task<JsonElement> MessageToAsync(string address, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var found = await http.GetFromJsonAsync<JsonElement>(new Uri(Api, $"search?query={Uri.EscapeDataString($"to:\"{address}\"")}"), cancellationToken).ConfigureAwait(false);
            if (found.GetProperty("messages").GetArrayLength() > 0)
            {
                var id = found.GetProperty("messages")[0].GetProperty("ID").GetString();
                return await http.GetFromJsonAsync<JsonElement>(new Uri(Api, $"message/{id}"), cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"No message to {address} in Mailpit.");
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await container.DisposeAsync().ConfigureAwait(false);
    }
}
