using System.Security.Claims;
using FluentValidation;
using Karry.BuildingBlocks.Api;
using Karry.BuildingBlocks.Idempotency;
using Karry.BuildingBlocks.Modules;
using Karry.BuildingBlocks.Tenancy;
using Karry.Modules.Identity.Application;
using Karry.Modules.Identity.Domain;
using Karry.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karry.Modules.Identity.Api;

/// <summary>Users, invitations and deactivation (US-P1-IAM-03). Routes under <c>/api/v1/identity</c>.</summary>
public sealed class IdentityModule : IModule
{
    private const int MaxPageSize = 200;

    public string Name => "identity";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Karry");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Host without a database (some host tests): endpoints still map and fail only when called.
            return;
        }

        services.AddIdentityInfrastructure(configuration, connectionString);
        services.AddScoped<IValidator<InviteUser>, InviteUserValidator>();
        services.AddScoped<IValidator<CreateFieldUser>, CreateFieldUserValidator>();
        services.AddScoped<IValidator<AcceptInvitation>, AcceptInvitationValidator>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/invitations", InviteAsync)
            .Validate<InviteUser>()
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("InviteUser");

        // The invitee has no account yet: the token is the credential and names the tenant itself.
        endpoints.MapPost("/invitations/accept", AcceptAsync)
            .AllowAnonymous()
            .AllowWithoutTenant()
            .Validate<AcceptInvitation>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("AcceptInvitation");

        endpoints.MapPost("/field-users", CreateFieldUserAsync)
            .Validate<CreateFieldUser>()
            .RequireIdempotencyKey()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("CreateFieldUser");

        endpoints.MapPost("/users/{id:guid}/deactivate", DeactivateAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("DeactivateUser");

        endpoints.MapGet("/users", ListAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithName("ListUsers");

        endpoints.MapGet("/users/{id:guid}", GetAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetUser");

        // The signed-in user's own profile (portal header, MFA hint). 404 when the token has no Karry user.
        endpoints.MapGet("/me", MeAsync)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetCurrentUser");
    }

    private static string? Subject(ClaimsPrincipal user) => user.FindFirst("sub")?.Value;

    private static UserResponse ToResponse(UserAccount u) =>
        new(u.Id.Value, u.Kind.ToString().ToLowerInvariant(), u.Status.ToString().ToLowerInvariant(), u.FirstName, u.LastName, u.Email, u.Phone, u.EmployeeId,
            [.. u.Roles.Select(r => new RoleAssignmentDto(r.Role, r.SiteId))], u.RequiresMfa, u.CreatedAt, u.DeactivatedAt);

    private static async Task<Results<Created<InvitedUser>, ProblemHttpResult>> InviteAsync(
        InviteUser request, ClaimsPrincipal caller, [FromServices] InviteUserHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, Subject(caller), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/identity/users/{result.Value.UserId}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> AcceptAsync(
        AcceptInvitation request, [FromServices] AcceptInvitationHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Created<UserResponse>, ProblemHttpResult>> CreateFieldUserAsync(
        CreateFieldUser request, ClaimsPrincipal caller, [FromServices] CreateFieldUserHandler handler, [FromServices] IdentityDbContext db,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, Subject(caller), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == new UserId(result.Value), cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/identity/users/{result.Value}", ToResponse(user));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeactivateAsync(
        Guid id, ClaimsPrincipal caller, [FromServices] DeactivateUserHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, Subject(caller), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<UserPage>, ProblemHttpResult>> ListAsync(
        ClaimsPrincipal caller, [FromServices] IIdentityStore store, [FromServices] IdentityDbContext db,
        [FromQuery] Guid? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var allowed = await RequireAdminAsync(store, caller, cancellationToken).ConfigureAwait(false);
        if (allowed is not null)
        {
            return allowed;
        }

        var size = Math.Clamp(limit ?? 50, 1, MaxPageSize);
        // Keyset pagination on the UUIDv7 key (creation order). The cursor condition is SQL (EF cannot compare a
        // value-converted ID; xmin, the concurrency token, is a system column that * does not return); the query filter
        // and row-level security still apply on top of it.
        var users = cursor is { } after
            ? db.Users.FromSql($"SELECT *, xmin FROM identity.users WHERE id > {after}")
            : db.Users;
        var page = await users.AsNoTracking().OrderBy(u => u.Id).Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = page.Take(size).Select(ToResponse).ToList();
        return TypedResults.Ok(new UserPage(items, page.Count > size ? items[^1].Id : null));
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> GetAsync(
        Guid id, ClaimsPrincipal caller, [FromServices] IIdentityStore store, CancellationToken cancellationToken)
    {
        var allowed = await RequireAdminAsync(store, caller, cancellationToken).ConfigureAwait(false);
        if (allowed is not null)
        {
            return allowed;
        }

        var user = id == Guid.Empty ? null : await store.FindUserAsync(new UserId(id), cancellationToken).ConfigureAwait(false);
        return user is null
            ? SharedKernel.Results.Error.NotFound("identity.user_not_found", "No such user in this company.").ToProblem()
            : TypedResults.Ok(ToResponse(user));
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> MeAsync(
        ClaimsPrincipal caller, [FromServices] IIdentityStore store, CancellationToken cancellationToken)
    {
        var user = Subject(caller) is { } subject ? await store.FindBySubjectAsync(subject, cancellationToken).ConfigureAwait(false) : null;
        return user is null
            ? SharedKernel.Results.Error.NotFound("identity.user_not_found", "This account has no Karry user in this company.").ToProblem()
            : TypedResults.Ok(ToResponse(user));
    }

    private static async Task<ProblemHttpResult?> RequireAdminAsync(IIdentityStore store, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        var user = Subject(caller) is { } subject ? await store.FindBySubjectAsync(subject, cancellationToken).ConfigureAwait(false) : null;
        return user is { Status: UserStatus.Active } && user.HasRole(Roles.TenantAdmin)
            ? null
            : SharedKernel.Results.Error.Forbidden("identity.admin_required", "Only a tenant administrator can manage users.").ToProblem();
    }
}

public sealed record RoleAssignmentDto(string Role, Guid? SiteId);

public sealed record UserResponse(
    Guid Id, string Kind, string Status, string FirstName, string LastName, string? Email, string? Phone, string? EmployeeId,
    IReadOnlyList<RoleAssignmentDto> Roles, bool RequiresMfa, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);

public sealed record UserPage(IReadOnlyList<UserResponse> Items, Guid? NextCursor);
