using System.Net;
using System.Text.Json;

namespace Karry.Host.IntegrationTests;

[Trait("Story", "US-P1-PLT-01")]
[Trait("TestCase", "TC-P1-PLT-022")]
public sealed class OpenApiTests
{
    [Fact]
    public async Task The_v1_api_description_is_published_outside_production()
    {
        await using var factory = new HostFactory(connectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        document.GetProperty("openapi").GetString().Should().StartWith("3.");
    }

    [Fact]
    public async Task The_api_description_is_not_published_in_production()
    {
        await using var factory = new HostFactory(connectionString: null, environment: "Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
