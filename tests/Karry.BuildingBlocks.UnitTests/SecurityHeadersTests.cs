using System.Net;
using Karry.BuildingBlocks.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// Hardening plan H-W2: every answer of the API carries the security headers, errors included, on a real Kestrel
/// server (the test server would not show the <c>Server</c> header).
/// </summary>
[Trait("Story", "P1.18a")]
public sealed class SecurityHeadersTests : IAsyncDisposable
{
    private readonly List<WebApplication> apps = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var app in apps)
        {
            await app.DisposeAsync();
        }
    }

    public static TheoryData<string, HttpStatusCode> Answers => new()
    {
        { "/ok", HttpStatusCode.OK },
        { "/no-such-route", HttpStatusCode.NotFound },
        { "/boom", HttpStatusCode.InternalServerError },
        { "/refused", HttpStatusCode.Conflict },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task Every_answer_carries_the_headers_and_kestrel_does_not_name_itself(string path, HttpStatusCode status)
    {
        using var client = await StartAsync("Production");

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(status);
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "X-Frame-Options").Should().Be("DENY");
        Header(response, "Referrer-Policy").Should().Be("no-referrer");
        Header(response, "Cross-Origin-Opener-Policy").Should().Be("same-origin");
        Header(response, "Content-Security-Policy").Should().Be(SecurityHeaders.ContentSecurityPolicy);
        // The exception handler sets its own "no-cache,no-store"; either way nothing is stored.
        Header(response, "Cache-Control").Should().Contain("no-store", "answers name people, prices and companies: never in a shared cache");
        response.Headers.Contains("Server").Should().BeFalse();
        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse("plain HTTP: HSTS means nothing there");
    }

    [Fact]
    public async Task An_endpoint_that_sets_its_own_caching_and_policy_keeps_them()
    {
        using var client = await StartAsync("Production");

        using var response = await client.GetAsync(new Uri("/logo", UriKind.Relative), TestContext.Current.CancellationToken);

        Header(response, "Cache-Control").Should().Be("public, max-age=300");
        Header(response, "Content-Security-Policy").Should().Be("default-src 'none'");
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    public async Task Https_answers_pin_https_outside_development(string environment, bool pinned)
    {
        using var client = await StartAsync(environment, https: true);

        using var response = await client.GetAsync(new Uri("/ok", UriKind.Relative), TestContext.Current.CancellationToken);

        if (pinned)
        {
            Header(response, "Strict-Transport-Security").Should().Be(SecurityHeaders.StrictTransportSecurity);
        }
        else
        {
            response.Headers.Contains("Strict-Transport-Security").Should().BeFalse("a browser must never pin HTTPS for localhost");
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? string.Join(", ", values) : null;

    /// <summary>A Kestrel app with the API conventions; <paramref name="https"/> marks requests as HTTPS as the trusted proxy would.</summary>
    private async Task<HttpClient> StartAsync(string environment, bool https = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddKarryApiConventions();
        var app = builder.Build();
        apps.Add(app);
        if (https)
        {
            app.Use((context, next) =>
            {
                context.Request.Scheme = "https";
                return next(context);
            });
        }

        app.UseKarryApiConventions();
        app.MapGet("/ok", () => new { fine = true });
        app.MapGet("/boom", string () => throw new InvalidOperationException("never shown"));
        app.MapGet("/refused", () => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict));
        app.MapGet("/logo", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=300";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'";
            return TypedResults.Bytes([0x89, 0x50, 0x4E, 0x47], "image/png");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
