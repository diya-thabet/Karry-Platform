using System.Security.Claims;
using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Billing.Application;
using Karry.Modules.Billing.Domain;
using Karry.Modules.Billing.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Karry.Modules.Billing.Api;

/// <summary>
/// Plans, subscriptions, entitlements and usage (US-P1-BIL-01 … 05, ADR-0023). Routes under <c>/api/v1/billing</c>.
/// The plan catalogue is platform data, managed by platform operators.
/// </summary>
public sealed class BillingModule : IModule
{
    public string Name => "billing";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map ([FromServices]) and fail only when called.
            return;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddBillingInfrastructure(connectionString);
        services.AddScoped<PublishPlanHandler>();
        services.AddScoped<IValidator<PublishPlan>, PublishPlanValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The plan catalogue (US-P1-BIL-01): platform operators only, no tenant in their token.
        endpoints.MapGet("/plans", ListPlansAsync)
            .AllowWithoutTenant()
            .RequireAuthorization(KarryPolicies.PlatformOperator)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ListPlans");

        endpoints.MapGet("/plans/{code}", PlanVersionsAsync)
            .AllowWithoutTenant()
            .RequireAuthorization(KarryPolicies.PlatformOperator)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetPlanVersions");

        // Publishes the next version of a plan (or a new plan); existing subscriptions keep theirs (AC2).
        endpoints.MapPost("/plans", PublishPlanAsync)
            .AllowWithoutTenant()
            .RequireAuthorization(KarryPolicies.PlatformOperator)
            .Validate<PublishPlan>()
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("PublishPlan");
    }

    private static async Task<Ok<PlanListResponse>> ListPlansAsync([FromServices] IPlanStore plans, CancellationToken cancellationToken)
    {
        var latest = await plans.LatestAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new PlanListResponse([.. latest.Select(PlanResponse.Of)]));
    }

    private static async Task<Results<Ok<PlanListResponse>, ProblemHttpResult>> PlanVersionsAsync(
        string code, [FromServices] IPlanStore plans, CancellationToken cancellationToken)
    {
        var versions = await plans.VersionsAsync(code, cancellationToken).ConfigureAwait(false);
        return versions.Count == 0
            ? SharedKernel.Results.Error.NotFound("billing.plan_not_found", "No plan has this code.").ToProblem()
            : TypedResults.Ok(new PlanListResponse([.. versions.Select(PlanResponse.Of)]));
    }

    private static async Task<Results<Created<PlanResponse>, ProblemHttpResult>> PublishPlanAsync(
        PublishPlan request, ClaimsPrincipal caller, [FromServices] PublishPlanHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, caller.FindFirst("sub")?.Value, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/billing/plans/{result.Value.Code}", PlanResponse.Of(result.Value))
            : result.Error.ToProblem();
    }
}

public sealed record PlanListResponse(IReadOnlyList<PlanResponse> Items);

/// <summary>
/// One plan version. <c>Limits</c>: a limit not listed is unlimited. <c>Prices</c>: empty for plans quoted by sales.
/// </summary>
public sealed record PlanResponse(Guid Id, string Code, int Version, string Name, IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, int> Limits, IReadOnlyList<PlanPriceDto> Prices, DateTimeOffset PublishedAt)
{
    public static PlanResponse Of(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(plan.Id.Value, plan.Code, plan.Version, plan.Name, plan.Features, plan.Limits,
            [.. plan.Prices.Select(PlanPriceDto.Of)], plan.PublishedAt);
    }
}
