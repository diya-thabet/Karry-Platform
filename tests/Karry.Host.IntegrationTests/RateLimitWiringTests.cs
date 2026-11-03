using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Karry.Host.IntegrationTests;

/// <summary>The real host applies the request limits (threat T-12; the limits themselves: <c>RateLimitTests</c>).</summary>
[Trait("Story", "P1.18a")]
public sealed class RateLimitWiringTests
{
    [Fact]
    public async Task Anonymous_enrolment_attempts_past_the_limit_get_429()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Karry", string.Empty);
            builder.UseSetting("RateLimits:AnonymousPerAddressPerMinute", "2");
        });
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var answered = await client.PostAsJsonAsync("/api/v1/identity/devices/enroll", new { }, TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);
        }

        using var refused = await client.PostAsJsonAsync("/api/v1/identity/devices/enroll", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"));
    }
}
