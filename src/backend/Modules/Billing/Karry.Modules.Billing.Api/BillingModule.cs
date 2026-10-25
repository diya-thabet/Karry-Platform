using System.Security.Claims;
using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Billing.Application;
using Karry.Modules.Billing.Domain;
using Karry.Modules.Billing.Infrastructure;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Authorization;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
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

    public IReadOnlyList<PermissionDefinition> Permissions => BillingPermissions.All;

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
        services.AddScoped<IValidator<ChangeSubscription>, ChangeSubscriptionValidator>();
        services.AddScoped<IValidator<RecordInvoice>, RecordInvoiceValidator>();
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

        // A tenant's subscription and invoices, managed by operators until the billing provider (US-P1-BIL-02).
        // The work runs in the tenant's own unit of work, so each change is in that tenant's ledger.
        var tenants = endpoints.MapGroup("/tenants/{tenantId:guid}")
            .AllowWithoutTenant()
            .RequireAuthorization(KarryPolicies.PlatformOperator)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tenants.MapGet("/subscription", OperatorSubscriptionAsync).WithName("GetTenantSubscription");

        tenants.MapPut("/subscription", ChangeSubscriptionAsync)
            .Validate<ChangeSubscription>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("ChangeTenantSubscription");

        tenants.MapPost("/subscription/actions", ActOnSubscriptionAsync)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("ActOnTenantSubscription");

        tenants.MapGet("/invoices", OperatorInvoicesAsync).WithName("ListTenantInvoices");

        tenants.MapPost("/invoices", RecordInvoiceAsync)
            .Validate<RecordInvoice>()
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("RecordTenantInvoice");

        tenants.MapPost("/invoices/{invoiceId:guid}/payment", PayInvoiceAsync)
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("PayTenantInvoice");

        // The company's own subscription and invoices, read-only ("Plan & usage").
        endpoints.MapGet("/subscription", CurrentSubscriptionAsync)
            .RequirePermission(BillingPermissions.SubscriptionRead)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetCurrentSubscription");

        endpoints.MapGet("/invoices", CurrentInvoicesAsync)
            .RequirePermission(BillingPermissions.SubscriptionRead)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ListCurrentInvoices");
    }

    private static async Task<Results<Ok<SubscriptionResponse>, ProblemHttpResult>> CurrentSubscriptionAsync(
        [FromServices] SubscriptionOperations operations, CancellationToken cancellationToken) =>
        await operations.CurrentAsync(cancellationToken).ConfigureAwait(false) is { } subscription
            ? TypedResults.Ok(SubscriptionResponse.Of(subscription))
            : SubscriptionOperations.NotFound.ToProblem();

    private static async Task<Ok<InvoiceListResponse>> CurrentInvoicesAsync([FromServices] SubscriptionOperations operations, CancellationToken cancellationToken) =>
        TypedResults.Ok(InvoiceListResponse.Of(await operations.InvoicesAsync(cancellationToken).ConfigureAwait(false)));

    private static Task<Results<Ok<SubscriptionResponse>, ProblemHttpResult>> OperatorSubscriptionAsync(
        Guid tenantId, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, async operations => await operations.CurrentAsync(cancellationToken).ConfigureAwait(false) is { } subscription
            ? Result.Success(subscription)
            : SubscriptionOperations.NotFound, SubscriptionResponse.Of, cancellationToken);

    private static Task<Results<Ok<SubscriptionResponse>, ProblemHttpResult>> ChangeSubscriptionAsync(
        Guid tenantId, ChangeSubscription request, ClaimsPrincipal caller, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, operations => operations.ChangeAsync(request, Actor(caller), cancellationToken), SubscriptionResponse.Of, cancellationToken);

    private static Task<Results<Ok<SubscriptionResponse>, ProblemHttpResult>> ActOnSubscriptionAsync(
        Guid tenantId, SubscriptionAction request, ClaimsPrincipal caller, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, operations => operations.ActAsync(request, Actor(caller), cancellationToken), SubscriptionResponse.Of, cancellationToken);

    private static Task<Results<Ok<InvoiceListResponse>, ProblemHttpResult>> OperatorInvoicesAsync(
        Guid tenantId, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, async operations => Result.Success(await operations.InvoicesAsync(cancellationToken).ConfigureAwait(false)),
            InvoiceListResponse.Of, cancellationToken);

    private static Task<Results<Ok<InvoiceResponse>, ProblemHttpResult>> RecordInvoiceAsync(
        Guid tenantId, RecordInvoice request, ClaimsPrincipal caller, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, operations => operations.RecordInvoiceAsync(request, Actor(caller), cancellationToken), InvoiceResponse.Of, cancellationToken);

    private static Task<Results<Ok<InvoiceResponse>, ProblemHttpResult>> PayInvoiceAsync(
        Guid tenantId, Guid invoiceId, PayInvoice request, ClaimsPrincipal caller, [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, operations => operations.PayInvoiceAsync(new InvoiceId(invoiceId), request, Actor(caller), cancellationToken),
            InvoiceResponse.Of, cancellationToken);

    /// <summary>
    /// An operator request for one tenant, in its own scope: the work runs as that tenant (RLS, query filter, its
    /// ledger) while the request itself stays a platform request (its idempotency record has no tenant). An unknown
    /// tenant is 404.
    /// </summary>
    private static async Task<Results<Ok<TResponse>, ProblemHttpResult>> InTenantAsync<TValue, TResponse>(IServiceScopeFactory scopes, Guid tenantId,
        Func<SubscriptionOperations, Task<Result<TValue>>> work, Func<TValue, TResponse> respond, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenant = new TenantId(tenantId);
        if (tenantId == Guid.Empty
            || await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync(tenant, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound("billing.tenant_not_found", "No company has this ID.").ToProblem();
        }

        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
        var result = await work(scope.ServiceProvider.GetRequiredService<SubscriptionOperations>()).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(respond(result.Value)) : result.Error.ToProblem();
    }

    private static string? Actor(ClaimsPrincipal caller) => caller.FindFirst("sub")?.Value;

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
            ? Error.NotFound("billing.plan_not_found", "No plan has this code.").ToProblem()
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

/// <summary>
/// A tenant's subscription. <c>Status</c>: trial, active, past_due, restricted, suspended, cancelled, expired,
/// deleted. <c>Period</c>: monthly or annual. Limits and features are resolved with the entitlements (slice 3).
/// </summary>
public sealed record SubscriptionResponse(Guid Id, string Status, DateTimeOffset StatusSince, DateTimeOffset? TrialEndsAt, DateTimeOffset? UnpaidSince,
    string PlanCode, int PlanVersion, string Period, int Sites, int ExtraAssets, IReadOnlyDictionary<string, int> AddOns,
    IReadOnlyList<string> ExtraFeatures, IReadOnlyDictionary<string, int> LimitOverrides)
{
    public static SubscriptionResponse Of(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var terms = subscription.Terms;
        return new(subscription.Id.Value, Subscription.Name(subscription.Status), subscription.StatusSince, subscription.TrialEndsAt,
            subscription.UnpaidSince, terms.PlanCode, terms.PlanVersion, PlanPriceDto.PeriodName(terms.Period), terms.Sites, terms.ExtraAssets,
            terms.AddOns, terms.ExtraFeatures, terms.LimitOverrides);
    }
}

public sealed record InvoiceListResponse(IReadOnlyList<InvoiceResponse> Items)
{
    public static InvoiceListResponse Of(IReadOnlyList<Invoice> invoices) => new([.. invoices.Select(InvoiceResponse.Of)]);
}

/// <summary>An invoice Karry issued (decimal amount in its currency's minor units; negative for a credit note).</summary>
public sealed record InvoiceResponse(Guid Id, string Number, decimal Amount, string Currency, DateOnly IssuedOn, DateOnly DueOn, DateOnly? PaidOn)
{
    public static InvoiceResponse Of(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new(invoice.Id.Value, invoice.Number, invoice.Amount, invoice.Currency, invoice.IssuedOn, invoice.DueOn, invoice.PaidOn);
    }
}
