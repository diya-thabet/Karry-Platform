using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

/// <summary>
/// Request bodies (hardening plan H-A4, H-A6), on a real Kestrel server (the test server does not enforce body sizes):
/// 1 MiB unless the endpoint declares more, refused with <c>413 request.too_large</c> before anything reads it, and
/// malformed or absurdly nested JSON is a 400, never a 500.
/// </summary>
[Trait("Story", "P1.18a")]
public sealed class RequestLimitTests : IAsyncLifetime
{
    private const int MiB = 1024 * 1024;

    private WebApplication? app;
    private HttpClient client = null!;
    private int endpointRuns;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddKarryApiConventions();
        app = builder.Build();
        app.UseKarryApiConventions();

        // Reads the whole body before the endpoint, as the idempotency middleware does to fingerprint a request.
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Read-Early"))
            {
                context.Request.EnableBuffering();
                await context.Request.Body.CopyToAsync(Stream.Null);
                context.Request.Body.Position = 0;
            }

            await next(context);
        });
        app.MapPost("/command", (Command command) =>
        {
            Interlocked.Increment(ref endpointRuns);
            return command.Name;
        });
        app.MapPost("/raw", async (HttpRequest request) =>
        {
            Interlocked.Increment(ref endpointRuns);
            var length = 0L;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(buffer)) > 0)
            {
                length += read;
            }

            return length;
        });
        app.MapPost("/upload", async (HttpRequest request) =>
        {
            Interlocked.Increment(ref endpointRuns);
            await request.Body.CopyToAsync(Stream.Null);
            return "stored";
        }).WithMaxBody(4 * MiB);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_declared_body_over_one_MiB_is_refused_before_the_endpoint_runs()
    {
        using var response = await client.SendAsync(Declared("/command", Json(new string('a', 2 * MiB))), TestContext.Current.CancellationToken);

        await AssertTooLargeAsync(response, MiB);
        Assert.Equal(0, endpointRuns);
    }

    [Fact]
    public async Task A_streamed_body_over_one_MiB_is_cut_off_while_it_arrives()
    {
        using var response = await client.PostAsync(new Uri("/raw", UriKind.Relative), Chunked(2 * MiB), TestContext.Current.CancellationToken);

        await AssertTooLargeAsync(response, MiB);
    }

    [Fact]
    public async Task The_limit_is_in_place_before_a_middleware_reads_the_body()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/raw", UriKind.Relative)) { Content = Chunked(2 * MiB) };
        request.Headers.Add("X-Read-Early", "1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertTooLargeAsync(response, MiB);
        Assert.Equal(0, endpointRuns);
    }

    [Fact]
    public async Task An_endpoint_that_declares_more_takes_it_and_no_more()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/upload", UriKind.Relative)) { Content = Chunked(3 * MiB) };
        request.Headers.Add("X-Read-Early", "1");
        using var accepted = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var refused = await client.SendAsync(Declared("/upload", new ByteArrayContent(new byte[5 * MiB])), TestContext.Current.CancellationToken);
        await AssertTooLargeAsync(refused, 4 * MiB);
    }

    [Fact]
    public async Task A_small_body_still_reaches_the_endpoint()
    {
        using var response = await client.PostAsJsonAsync(new Uri("/command", UriKind.Relative), new Command("weigh"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("weigh", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public static TheoryData<string, string> BadJson => new()
    {
        { "cut short", "{\"name\":" },
        { "not JSON", "<xml/>" },
        { "wrong type", "[1,2,3]" },
        { "nested 5000 deep", "{\"name\":\"x\",\"more\":" + string.Concat(Enumerable.Repeat("[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000)) + "}" },
        { "empty", "" },
    };

    [Theory]
    [MemberData(nameof(BadJson))]
    public async Task Malformed_json_is_a_400_problem_never_a_500(string kind, string body)
    {
        using var response = await client.PostAsync(new Uri("/command", UriKind.Relative), new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{kind}: {(int)response.StatusCode}");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(ProblemTypes.ValidationCode, problem.GetProperty("code").GetString());
    }

    private static async Task AssertTooLargeAsync(HttpResponseMessage response, long maxBytes)
    {
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(RequestLimits.TooLargeCode, problem.GetProperty("code").GetString());
        Assert.Equal(maxBytes, problem.GetProperty("maxBytes").GetInt64());
    }

    [Fact]
    public async Task Invalid_utf8_is_a_400_problem()
    {
        byte[] bytes = [.. "{\"name\":\""u8, 0xC3, 0x28, 0xFF, .. "\"}"u8];
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync(new Uri("/command", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, endpointRuns);
    }

    /// <summary>With <c>Expect: 100-continue</c>, as large uploads do: the server answers before the body is sent.</summary>
    private static HttpRequestMessage Declared(string path, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = content };
        request.Headers.ExpectContinue = true;
        return request;
    }

    private static StringContent Json(string name) => new(JsonSerializer.Serialize(new Command(name)), Encoding.UTF8, "application/json");

    /// <summary>A body without <c>Content-Length</c>: the server only learns its size while reading it.</summary>
    private static StreamContent Chunked(int bytes)
    {
        var content = new StreamContent(new UnknownLengthStream(bytes));
        content.Headers.ContentLength = null;
        return content;
    }

    private sealed record Command(string Name);

    private sealed class UnknownLengthStream(int length) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, length - position);
            Array.Fill(buffer, (byte)'a', offset, n);
            position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
