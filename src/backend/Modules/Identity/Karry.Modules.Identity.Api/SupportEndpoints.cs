using System.Security.Claims;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Authorization;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Tenancy.Contracts;
using Karry.SharedKernel.Results;
using Karry.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Identity.Api;

/// <summary>A support session as the company and Karry see it (the operator's account ID is not shown to the company).</summary>
public sealed record SupportSessionResponse(
    Guid Id, Guid TenantId, string Mode, string? OperatorName, string? Reason, DateTimeOffset StartedAt, DateTimeOffset EndsAt,
    DateTimeOffset? EndedAt, bool Active)
{
    public static SupportSessionResponse Of(SupportSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(
            session.Id.Value, session.TenantId.Value, session.Mode == SupportMode.Consent ? "consent" : "break_glass", session.OperatorName,
            session.Reason, session.StartedAt, session.EndsAt, session.EndedAt, session.IsActive(now));
    }
}

public sealed record SupportSessionListResponse(IReadOnlyList<SupportSessionResponse> Items);

/// <summary>
/// Support access (ADR-0029 §4, US-P1-OPS-03 AC3). The company: support codes, its sessions, "End now". Karry's
/// platform operators: open a session on a company with its code or by break-glass, list and end theirs; that work
/// runs under the company's tenant, so it is in the company's ledger.
/// </summary>
internal static class SupportEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // Letting someone outside the company read its data: the Tenant Admin must have signed in recently (step-up).
        endpoints.MapPost("/support/consents", CreateConsentAsync)
            .RequirePermission(IdentityPermissions.SupportManage)
            .RequireRecentSignIn()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("CreateSupportConsent");

        endpoints.MapGet("/support/sessions", ListAsync)
            .RequirePermission(IdentityPermissions.SupportManage)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ListSupportSessions");

        endpoints.MapPost("/support/sessions/{id:guid}/end", EndAsync)
            .RequirePermission(IdentityPermissions.SupportManage)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("EndSupportSession");

        var operators = endpoints.MapGroup("/tenants/{tenantId:guid}/support-sessions")
            .AllowWithoutTenant()
            .RequireAuthorization(KarryPolicies.PlatformOperator)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        operators.MapGet("/", OperatorListAsync).WithName("ListTenantSupportSessions");

        operators.MapPost("/", StartAsync)
            .Validate<StartSupportSession>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("StartSupportSession");

        operators.MapPost("/{id:guid}/end", OperatorEndAsync)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("EndTenantSupportSession");
    }

    private static async Task<Results<Created<SupportConsentResponse>, ProblemHttpResult>> CreateConsentAsync(
        [FromServices] SupportAccess support, CancellationToken cancellationToken)
    {
        var result = await support.CreateConsentAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/identity/support/consents/{result.Value.ConsentId}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Ok<SupportSessionListResponse>> ListAsync(
        [FromServices] SupportAccess support, [FromServices] TimeProvider clock, CancellationToken cancellationToken)
    {
        var sessions = await support.ListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        return TypedResults.Ok(new SupportSessionListResponse([.. sessions.Select(s => SupportSessionResponse.Of(s, now))]));
    }

    private static async Task<Results<Ok<SupportSessionResponse>, ProblemHttpResult>> EndAsync(
        Guid id, [FromServices] SupportAccess support, [FromServices] TimeProvider clock, CancellationToken cancellationToken)
    {
        var result = await support.EndAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(SupportSessionResponse.Of(result.Value, clock.GetUtcNow())) : result.Error.ToProblem();
    }

    private static Task<Results<Ok<SupportSessionListResponse>, ProblemHttpResult>> OperatorListAsync(
        Guid tenantId, [FromServices] IServiceScopeFactory scopes, [FromServices] TimeProvider clock, CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, async sessions => Result.Success(await sessions.ListAsync(cancellationToken).ConfigureAwait(false)),
            list => new SupportSessionListResponse([.. list.Select(s => SupportSessionResponse.Of(s, clock.GetUtcNow()))]), cancellationToken);

    private static async Task<Results<Created<SupportSessionResponse>, ProblemHttpResult>> StartAsync(
        Guid tenantId, StartSupportSession request, ClaimsPrincipal caller, [FromServices] IServiceScopeFactory scopes, [FromServices] TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var result = await InTenantAsync(scopes, tenantId, sessions => sessions.StartAsync(request, Operator(caller), cancellationToken),
            session => SupportSessionResponse.Of(session, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return result.Result switch
        {
            Ok<SupportSessionResponse> ok => TypedResults.Created($"/api/v1/identity/tenants/{tenantId}/support-sessions/{ok.Value!.Id}", ok.Value),
            ProblemHttpResult problem => problem,
            _ => throw new InvalidOperationException("Unexpected result."),
        };
    }

    private static Task<Results<Ok<SupportSessionResponse>, ProblemHttpResult>> OperatorEndAsync(
        Guid tenantId, Guid id, ClaimsPrincipal caller, [FromServices] IServiceScopeFactory scopes, [FromServices] TimeProvider clock,
        CancellationToken cancellationToken) =>
        InTenantAsync(scopes, tenantId, sessions => sessions.EndAsync(id, Operator(caller), cancellationToken),
            session => SupportSessionResponse.Of(session, clock.GetUtcNow()), cancellationToken);

    /// <summary>Runs the work in the company's own scope (tenant context, row-level security, ledger).</summary>
    private static async Task<Results<Ok<TResponse>, ProblemHttpResult>> InTenantAsync<TValue, TResponse>(IServiceScopeFactory scopes, Guid tenantId,
        Func<SupportSessions, Task<Result<TValue>>> work, Func<TValue, TResponse> respond, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenant = new TenantId(tenantId);
        if (tenantId == Guid.Empty
            || await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync(tenant, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound("support.tenant_not_found", "No company has this ID.").ToProblem();
        }

        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant);
        var result = await work(scope.ServiceProvider.GetRequiredService<SupportSessions>()).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.Ok(respond(result.Value)) : result.Error.ToProblem();
    }

    private static SupportOperator Operator(ClaimsPrincipal caller)
    {
        var subject = caller.FindFirst("sub")?.Value ?? throw new InvalidOperationException("An operator token without a subject.");
        var name = caller.FindFirst("name")?.Value
            ?? string.Join(' ', new[] { caller.FindFirst("given_name")?.Value, caller.FindFirst("family_name")?.Value }.Where(n => !string.IsNullOrWhiteSpace(n)));
        return new SupportOperator(subject, string.IsNullOrWhiteSpace(name) ? null : name);
    }
}
