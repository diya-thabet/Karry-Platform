using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.BuildingBlocks.UnitTests;

[Trait("Story", "US-P1-PLT-01")]
public sealed class ApiConventionsTests : IAsyncLifetime
{
    private WebApplication? app;
    private HttpClient? client;

    public sealed record CreateThing(string Code, decimal Hours);

    public sealed class CreateThingValidator : AbstractValidator<CreateThing>
    {
        public CreateThingValidator()
        {
            RuleFor(x => x.Code).NotEmpty().MaximumLength(10);
            RuleFor(x => x.Hours).GreaterThanOrEqualTo(0m);
        }
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddKarryApiConventions();
        builder.Services.AddScoped<IValidator<CreateThing>, CreateThingValidator>();
        app = builder.Build();
        app.UseKarryApiConventions();

        app.MapGet("/ok", () => Result.Success(42).ToHttpResult());
        app.MapGet("/errors/{kind}", (ErrorKind kind) => (kind switch
        {
            ErrorKind.Validation => Error.Validation("thing.invalid", "Thing is invalid."),
            ErrorKind.NotFound => Error.NotFound("thing.not_found", "Thing not found."),
            ErrorKind.Conflict => Error.Conflict("thing.duplicate_code", "Code already used."),
            ErrorKind.Forbidden => Error.Forbidden("thing.forbidden", "Not allowed."),
            _ => Error.BusinessRule("meter.backwards", "Hour meters never go backwards."),
        }).ToProblem());
        app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code)); // as the authorization middleware answers
        app.MapGet("/boom", IResult () => throw new InvalidOperationException("Npgsql: password authentication failed for user karry_app at 10.0.0.5"));
        app.MapPost("/things", (CreateThing thing) => TypedResults.Created($"/things/{thing.Code}", thing)).Validate<CreateThing>();

        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_success_result_is_returned_as_ok()
    {
        var response = await client!.GetAsync(new Uri("/ok", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<int>(TestContext.Current.CancellationToken)).Should().Be(42);
    }

    [Theory]
    [Trait("TestCase", "TC-P1-PLT-019")]
    [InlineData(ErrorKind.Validation, 400, "thing.invalid")]
    [InlineData(ErrorKind.Forbidden, 403, "thing.forbidden")]
    [InlineData(ErrorKind.NotFound, 404, "thing.not_found")]
    [InlineData(ErrorKind.Conflict, 409, "thing.duplicate_code")]
    [InlineData(ErrorKind.BusinessRule, 422, "meter.backwards")]
    public async Task Each_error_kind_maps_to_its_status_and_a_problem_with_a_stable_code(ErrorKind kind, int status, string code)
    {
        var response = await client!.GetAsync(new Uri($"/errors/{kind}", UriKind.Relative), TestContext.Current.CancellationToken);
        var problem = await ReadProblem(response);

        ((int)response.StatusCode).Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("type").GetString().Should().Be(ProblemTypes.BaseUri + code);
        problem.GetProperty("status").GetInt32().Should().Be(status);
        problem.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-021")]
    public async Task An_unhandled_exception_returns_a_generic_500_that_leaks_nothing()
    {
        var response = await client!.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonDocument.Parse(body).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        problem.GetProperty("code").GetString().Should().Be(ProblemTypes.UnexpectedCode);
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        body.Should().NotContainAny("Npgsql", "password", "karry_app", "10.0.0.5", "InvalidOperationException", "at ");
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-020")]
    public async Task Invalid_input_is_rejected_with_field_errors_before_the_handler_runs()
    {
        var response = await client!.PostAsJsonAsync("/things", new CreateThing("", -1m), TestContext.Current.CancellationToken);
        var problem = await ReadProblem(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        problem.GetProperty("code").GetString().Should().Be(ProblemTypes.ValidationCode);
        var errors = problem.GetProperty("errors");
        errors.GetProperty("Code").EnumerateArray().Should().NotBeEmpty();
        errors.GetProperty("Hours").EnumerateArray().Should().NotBeEmpty();
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-020")]
    public async Task Valid_input_reaches_the_handler()
    {
        var response = await client!.PostAsJsonAsync("/things", new CreateThing("EX-01", 12.5m), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("TestCase", "TC-P1-PLT-019")]
    public async Task Framework_errors_use_the_same_problem_shape()
    {
        var notFound = await client!.GetAsync(new Uri("/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);
        var badJson = await client.PostAsync(
            new Uri("/things", UriKind.Relative),
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblem(notFound)).GetProperty("code").GetString().Should().Be("route.not_found");
        badJson.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblem(badJson)).GetProperty("code").GetString().Should().Be(ProblemTypes.ValidationCode);
    }

    [Theory]
    [InlineData(401, "auth.unauthenticated")]
    [InlineData(403, "auth.forbidden")]
    public async Task A_refusal_by_the_framework_says_what_it_is_not_an_unexpected_error(int status, string code)
    {
        var response = await client!.GetAsync(new Uri($"/status/{status}", UriKind.Relative), TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(status);
        (await ReadProblem(response)).GetProperty("code").GetString().Should().Be(code);
    }

    private static async Task<JsonElement> ReadProblem(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
}
