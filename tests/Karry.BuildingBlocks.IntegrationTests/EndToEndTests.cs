using System.Net;
using System.Net.Http.Json;
using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Outbox;
using Karry.BuildingBlocks.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.IntegrationTests;

/// <summary>
/// The whole P1.3 backbone in one host: HTTP (validation, idempotency, problem details) → module command →
/// aggregate + outbox in one transaction → dispatcher (hosted service) → subscriber projection.
/// </summary>
[Collection(PostgresGroup.Name)]
[Trait("Story", "US-P1-PLT-01")]
public sealed class EndToEndTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplication app = null!;
    private HttpClient client = null!;

    public sealed record CreateProbe(string Name);

    public sealed class CreateProbeValidator : AbstractValidator<CreateProbe>
    {
        public CreateProbeValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(50);
    }

    /// <summary>Test module wired exactly like a production module (IModule, endpoint group, DbContext).</summary>
    private sealed class ProbeModule(string connectionString, SemaphoreSlim? entered, SemaphoreSlim? gate) : IModule
    {
        public string Name => "probe";

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddModuleDbContext<ProbeDbContext>(connectionString);
            services.AddScoped<IValidator<CreateProbe>, CreateProbeValidator>();
            services.AddScoped<IDomainEventSubscriber<ProbeRenamed>, LogSubscriber>();
            services.AddOutboxDispatcher<ProbeDbContext>(new OutboxOptions { PollInterval = TimeSpan.FromMilliseconds(50) });
            services.AddOutboxDispatcherService<ProbeDbContext>();
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
            endpoints.MapPost("/probes", async (CreateProbe request, ProbeDbContext db, TimeProvider clock, CancellationToken ct) =>
                {
                    if (gate is not null)
                    {
                        entered!.Release(); // the key is reserved: the middleware ran before the handler
                        await gate.WaitAsync(ct);
                    }

                    var probe = Probe.Create(clock, request.Name);
                    db.Probes.Add(probe);
                    await db.SaveChangesAsync(ct);
                    return TypedResults.Created($"/api/v1/probe/probes/{probe.Id.Value}", new { id = probe.Id.Value, probe.Name });
                })
                .Validate<CreateProbe>()
                .RequireIdempotencyKey();
    }

    private SemaphoreSlim? gate;
    private SemaphoreSlim? entered;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }

        gate?.Dispose();
        entered?.Dispose();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-023")]
    public async Task A_command_flows_from_http_through_the_outbox_to_a_subscriber()
    {
        await StartAsync();

        var response = await PostAsync("key-e2e-1", new CreateProbe("crusher C1"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<CreatedProbe>(TestContext.Current.CancellationToken))!.Id;

        var projected = await WaitUntilAsync(async db => await db.Logs.AnyAsync(l => l.ProbeId == id, TestContext.Current.CancellationToken));
        projected.Should().BeTrue("the hosted dispatcher delivers the event to the subscriber");
        await using var check = Db();
        (await check.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken)).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-008")]
    public async Task Retrying_with_the_same_key_replays_the_first_response_without_executing_again()
    {
        await StartAsync();

        var first = await PostAsync("key-retry", new CreateProbe("loader L1"));
        var second = await PostAsync("key-retry", new CreateProbe("loader L1"));

        second.StatusCode.Should().Be(first.StatusCode).And.Be(HttpStatusCode.Created);
        (await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        second.Headers.GetValues(IdempotencyOptions.ReplayedHeaderName).Should().Equal("true");
        first.Headers.Contains(IdempotencyOptions.ReplayedHeaderName).Should().BeFalse();
        await using var check = Db();
        (await check.Probes.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "the command ran once");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-008")]
    public async Task Reusing_a_key_for_a_different_request_is_refused()
    {
        await StartAsync();

        await PostAsync("key-reuse", new CreateProbe("truck T1"));
        var reuse = await PostAsync("key-reuse", new CreateProbe("truck T2"));

        reuse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await reuse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("idempotency.key_reused");
        await using var check = Db();
        (await check.Probes.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-008")]
    public async Task A_request_without_a_key_is_refused_before_anything_runs()
    {
        await StartAsync();

        var response = await client.PostAsJsonAsync("/api/v1/probe/probes", new CreateProbe("no key"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("idempotency.key_required");
        await using var check = Db();
        (await check.Probes.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-008")]
    public async Task A_concurrent_duplicate_gets_409_while_the_first_is_running_and_the_command_runs_once()
    {
        gate = new SemaphoreSlim(0);
        entered = new SemaphoreSlim(0);
        await StartAsync();

        var first = PostAsync("key-concurrent", new CreateProbe("screen S1"));
        (await entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Should().BeTrue("the first request reaches the handler");
        var duplicate = await PostAsync("key-concurrent", new CreateProbe("screen S1"));
        gate.Release();

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("idempotency.request_in_progress");
        (await first).StatusCode.Should().Be(HttpStatusCode.Created);
        await using var check = Db();
        (await check.Probes.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-008")]
    public async Task An_invalid_request_is_stored_too_so_its_retry_gets_the_same_400()
    {
        await StartAsync();

        var first = await PostAsync("key-invalid", new CreateProbe(""));
        var retry = await PostAsync("key-invalid", new CreateProbe(""));

        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        retry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        retry.Headers.Contains(IdempotencyOptions.ReplayedHeaderName).Should().BeTrue();
    }

    private sealed record CreatedProbe(Guid Id, string Name);

    private async Task StartAsync()
    {
        var connectionString = postgres.NewDatabase();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddKarryApiConventions();
        builder.Services.AddIdempotency(connectionString);
        builder.Services.AddModules(builder.Configuration, [new ProbeModule(connectionString, entered, gate)]);
        app = builder.Build();
        app.UseKarryApiConventions();
        app.UseIdempotency();
        app.MapModules();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProbeDbContext>().Database.EnsureCreatedAsync();
            // Second context in the same database: create its schema objects too.
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await platform.Database.ExecuteSqlRawAsync(platform.Database.GenerateCreateScript());
        }

        await app.StartAsync();
        client = app.GetTestClient();
    }

    private Task<HttpResponseMessage> PostAsync(string key, CreateProbe body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/probe/probes") { Content = JsonContent.Create(body) };
        request.Headers.Add(IdempotencyOptions.HeaderName, key);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private ProbeDbContext Db() => app.Services.CreateScope().ServiceProvider.GetRequiredService<ProbeDbContext>();

    private async Task<bool> WaitUntilAsync(Func<ProbeDbContext, Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            await using var db = Db();
            if (await condition(db))
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }
}
