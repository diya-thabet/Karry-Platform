using System.Net;

namespace Karry.Host.IntegrationTests;

[Trait("Story", "US-P1-PLT-02")]
[Trait("TestCase", "TC-P1-PLT-012")]
public sealed class HealthEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // Port 1 is never a PostgreSQL server: connection is refused immediately.
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=karry;Username=nobody;Password=nothing;Timeout=2";

    [Fact]
    public async Task Live_is_healthy_even_without_a_database()
    {
        await using var factory = new HostFactory(connectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("Healthy");
    }

    [Fact]
    public async Task Ready_is_unavailable_when_no_database_is_configured()
    {
        await using var factory = new HostFactory(connectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Ready_is_unavailable_and_reveals_nothing_when_the_database_is_unreachable()
    {
        await using var factory = new HostFactory(UnreachableDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Be("Unhealthy", "infrastructure details (hosts, ports, errors) must never leak through a public endpoint");
    }

    [Fact]
    public async Task Ready_is_healthy_when_the_database_answers()
    {
        await using var factory = new HostFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("Healthy");
    }
}
